using System.Diagnostics;
using System.Globalization;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Ocr;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf;

public sealed record OcrPdfOptions(string Language);

public sealed class OcrPdfAction : PerFileAction<OcrPdfOptions>
{
    private readonly IPdfPageRenderer? _renderer;
    private readonly ITextRecognizer? _textRecognizer;

    public OcrPdfAction(FileActionServices services, IPdfPageRenderer? renderer, ITextRecognizer? textRecognizer) : base(services)
    {
        _renderer = renderer;
        _textRecognizer = textRecognizer;

        var availableLanguages = _textRecognizer?.AvailableLanguages ?? Array.Empty<string>();
        var userLanguage = CultureInfo.CurrentUICulture.Name;
        var defaultLanguage = availableLanguages.FirstOrDefault(l => string.Equals(l, userLanguage, StringComparison.OrdinalIgnoreCase)) ?? availableLanguages.FirstOrDefault() ?? string.Empty;

        var choices = availableLanguages.Select(l => new ActionParameterChoice(l, l)).ToList();

        var param = availableLanguages.Count > 0
            ? ActionParameterDefinition.Choice("Language", SR.GetString("OcrPdfAction_ParamTitle_Language"), defaultLanguage, choices)
            : ActionParameterDefinition.Text("Language", SR.GetString("OcrPdfAction_ParamTitle_Language"), string.Empty);

        Descriptor = new ActionDescriptor(
            "contextbar.pdf.ocr",
            SR.GetString("OcrPdfAction_Title"),
            SR.GetString("OcrPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "ocr", "searchable", "text" },
            RequiresPreview: true,
            ChangesFiles: true,
            Parameters: new[] { param },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }

    protected override FileInputFilter Input => PdfSupport.PdfInput;

    public (bool IsAvailable, string? Reason) CheckAvailability()
    {
        if (_renderer == null || !_renderer.IsAvailable) return (false, SR.GetString("OcrPdfAction_RendererUnavailable"));
        if (_textRecognizer == null) return (false, SR.GetString("OcrPdfAction_RecognizerUnavailable"));
        if (_textRecognizer.AvailableLanguages.Count == 0) return (false, SR.GetString("OcrPdfAction_NoLanguagesInstalled"));
        return (true, null);
    }

    protected override string? UnavailableReason => CheckAvailability().IsAvailable ? null : CheckAvailability().Reason;

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        var (isAvailable, reason) = CheckAvailability();
        if (!isAvailable)
        {
            return new ActionPreview(true, null, Array.Empty<string>(), new[] { reason ?? "OCR is not available." }, IsBlocked: true);
        }
        return await base.PreviewAsync(context, parameters, cancellationToken);
    }

    protected override OcrPdfOptions ReadOptions(ActionParameterValues v)
    {
        var language = _textRecognizer?.AvailableLanguages.Count > 0 ? v.GetChoice("Language") : v.GetText("Language");
        return new OcrPdfOptions(language);
    }

    protected override string DescribeOperation(int n, OcrPdfOptions o) => SR.GetString("OcrPdfAction_Message");

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile f, OcrPdfOptions o)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(f.Name) + SR.GetString("OcrPdfAction_OutputSuffix"), f.Extension);
    }

    protected override Task ProcessAsync(FileWorkItem item, OcrPdfOptions o, FileOutputWriter output, CancellationToken ct) =>
        output.WriteFileAsync(item.Directory, item.BaseName + SR.GetString("OcrPdfAction_OutputSuffix"), item.File.Extension,
            async (stream, token) =>
            {
                var doc = await PdfSupport.LoadAsync(item.File.Path, token).ConfigureAwait(false);
                var builder = new PdfDocumentBuilder();
                bool anyPageProcessed = false;

                for (int i = 0; i < doc.PageCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var text = PdfText.Extract(doc, i);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // Page already has text, leave it untouched.
                        builder.AddPage(doc, i);
                        continue;
                    }

                    // No text, need OCR
                    anyPageProcessed = true;
                    var raster = await _renderer!.RenderGrayAsync(item.File.Path, i, 4000, token).ConfigureAwait(false);
                    // Convert Gray8 to Bgra32
                    byte[] bgra32 = new byte[raster.Width * raster.Height * 4];
                    for (int p = 0; p < raster.Gray8.Length; p++)
                    {
                        byte g = raster.Gray8[p];
                        bgra32[p * 4] = g;
                        bgra32[p * 4 + 1] = g;
                        bgra32[p * 4 + 2] = g;
                        bgra32[p * 4 + 3] = 255;
                    }

                    var lines = await _textRecognizer!.RecognizeAsync(raster.Width, raster.Height, bgra32, o.Language, token).ConfigureAwait(false);

                    var decorations = new List<PdfDecoration>();

                    var pageDict = doc.Pages[i].Dictionary;
                    var visibleBox = pageDict["CropBox"] as PdfArray ?? pageDict["MediaBox"] as PdfArray;
                    double[] box = visibleBox?.Items.Select(x => x is PdfReal r ? r.Value : (x as PdfInteger)?.Value ?? 0.0).ToArray() ?? new double[] { 0, 0, 612, 792 };
                    if (box.Length < 4) box = new double[] { 0, 0, 612, 792 };

                    double boxW = box[2] - box[0];
                    double boxH = box[3] - box[1];

                    foreach (var line in lines)
                    {
                        foreach (var word in line.Words)
                        {
                            if (string.IsNullOrWhiteSpace(word.Text)) continue;

                            double pdfX = word.X / raster.Width * boxW;
                            double pdfY = boxH - ((word.Y + word.Height) / raster.Height * boxH);
                            double pdfW = word.Width / raster.Width * boxW;
                            double pdfH = word.Height / raster.Height * boxH;

                            double fontSize = pdfH;

                            double unscaledWidth = PdfTextMeasurement.GetTextWidth(word.Text, PdfStandardFont.Helvetica, fontSize);
                            double tz = unscaledWidth > 0 ? (pdfW / unscaledWidth * 100.0) : 100.0;

                            // Adjusting for PdfAnchor.BottomLeft logic
                            var dec = new PdfTextDecoration(
                                word.Text,
                                PdfAnchor.BottomLeft,
                                fontSize,
                                new PdfColor(0, 0, 0),
                                OffsetXPt: pdfX,
                                OffsetYPt: pdfY,
                                MarginPt: 0,
                                Invisible: true,
                                HorizontalScaling: tz);
                            decorations.Add(dec);
                        }
                    }

                    builder.AddPage(doc, i, 0, decorations);
                }

                if (!anyPageProcessed)
                {
                    throw new FileSkippedException(SR.GetString("OcrPdfAction_Skipped_HasText"));
                }

                await PdfSupport.RunAsync(() => builder.Save(stream), token).ConfigureAwait(false);
            }, ct);
}
