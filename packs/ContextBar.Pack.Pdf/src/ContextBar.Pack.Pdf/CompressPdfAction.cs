using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions;
using ContextBar.Abstractions.Files;
using FileKit.Pdf;

namespace ContextBar.Pack.Pdf;

public sealed record CompressPdfOptions(string Level);

public sealed class CompressPdfAction : PerFileAction<CompressPdfOptions>
{
    private readonly IPdfImageRecompressor? _recompressor;

    public CompressPdfAction(FileActionServices services, IPdfImageRecompressor? recompressor = null) : base(services)
    {
        _recompressor = recompressor;
        Descriptor = new ActionDescriptor(
            "contextbar.pdf.compress", SR.GetString("CompressPdfAction_Title"),
            SR.GetString("CompressPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "compress", "shrink", "reduce", "size" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("Level", SR.GetString("CompressPdfAction_ParamTitle_Level"), "balanced", new[]
                {
                    new ActionParameterChoice("light", SR.GetString("CompressPdfAction_ParamChoice_Light")),
                    new ActionParameterChoice("balanced", SR.GetString("CompressPdfAction_ParamChoice_Balanced")),
                    new ActionParameterChoice("strong", SR.GetString("CompressPdfAction_ParamChoice_Strong"))
                })
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override CompressPdfOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetChoice("Level"));

    protected override string DescribeOperation(int inputCount, CompressPdfOptions options) =>
        inputCount == 1 ? SR.GetString("CompressPdfAction_Operation_One") : SR.GetString("CompressPdfAction_Operation_Many");

    protected override bool ReportSizeChange => true;

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, CompressPdfOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (compressed)", ".pdf");
    }

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        var preview = await base.PreviewAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        var options = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
        var warnings = new List<string>(preview.Warnings);

        if (options.Level == "balanced" || options.Level == "strong")
        {
            warnings.Add(SR.GetString("CompressPdfAction_Warning_QualityReduced"));
        }

        int totalUnsupported = 0;
        if (context.Content is FileSelection selection)
        {
            foreach (var item in selection.Files.Where(f => PdfSupport.PdfInput.Accepts(f)))
            {
                try
                {
                    if (item.Path != null)
                    {
                        var doc = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
                        var images = await Task.Run(() => PdfImages.Enumerate(doc), cancellationToken).ConfigureAwait(false);
                        totalUnsupported += images.Count(i => i.ColorSpace is "DeviceCMYK" or "Indexed" or "ICCBased" || i.Filter == "JPXDecode" || i.HasSoftMask);
                    }
                }
                catch
                {
                    // Ignore load errors in preview
                }
            }
        }

        if (totalUnsupported > 0)
        {
            warnings.Add(string.Format(SR.GetString("CompressPdfAction_Warning_UnsupportedImages"), totalUnsupported));
        }

        return preview with { Warnings = warnings };
    }

    protected override async Task ProcessAsync(FileWorkItem item, CompressPdfOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        long initialSize = new FileInfo(item.Path).Length;
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);

        int unsupportedCount = 0;
        await output.WriteFileAsync(item.Directory, item.BaseName + " (compressed)", ".pdf",
            (stream, ct) => Task.Run(async () =>
            {
                try
                {
                    var writeOptions = new PdfWriteOptions { Compact = true };
                    var builder = new PdfDocumentBuilder();
                    builder.AddPages(document, Enumerable.Range(0, document.PageCount));

                    if (options.Level != "light" && _recompressor != null)
                    {
                        int targetDpi = options.Level == "strong" ? 110 : 150;
                        int quality = options.Level == "strong" ? 60 : 75;

                        var images = PdfImages.Enumerate(document);
                        foreach (var image in images)
                        {
                            ct.ThrowIfCancellationRequested();

                            if (image.ColorSpace is "DeviceCMYK" or "Indexed" or "ICCBased" ||
                                image.Filter == "JPXDecode" || image.HasSoftMask)
                            {
                                Interlocked.Increment(ref unsupportedCount);
                                continue;
                            }

                            double placedWidthInches = 0;
                            double placedHeightInches = 0;

                            if (image.PageIndices.Count > 0)
                            {
                                foreach (int pageIndex in image.PageIndices)
                                {
                                    var (wPt, hPt) = PdfTools.GetPageSize(document, pageIndex);
                                    placedWidthInches = Math.Max(placedWidthInches, wPt / 72.0);
                                    placedHeightInches = Math.Max(placedHeightInches, hPt / 72.0);
                                }
                            }

                            bool needsRecompression = false;
                            if (placedWidthInches > 0 && placedHeightInches > 0)
                            {
                                double currentDpiX = image.Width / placedWidthInches;
                                double currentDpiY = image.Height / placedHeightInches;
                                if (currentDpiX > targetDpi || currentDpiY > targetDpi || image.Width > 2000 || image.Height > 2000)
                                {
                                    needsRecompression = true;
                                }
                            }
                            else if (image.Width > 2000 || image.Height > 2000)
                            {
                                needsRecompression = true;
                            }

                            if (needsRecompression)
                            {
                                byte[]? encoded = null;
                                if (image.Filter == "FlateDecode")
                                {
                                    encoded = PdfImages.TryDecodeFlatePixels(document, image);
                                }
                                else
                                {
                                    encoded = PdfImages.GetEncodedData(document, image);
                                }

                                if (encoded != null)
                                {
                                    int maxWidth = placedWidthInches > 0 ? (int)(placedWidthInches * targetDpi) : image.Width;
                                    int maxHeight = placedHeightInches > 0 ? (int)(placedHeightInches * targetDpi) : image.Height;

                                    var replacement = await _recompressor.RecompressAsync(encoded, image.Filter, image, maxWidth, maxHeight, quality, ct).ConfigureAwait(false);
                                    if (replacement != null)
                                    {
                                        builder.ReplaceImage(document, image.Reference, replacement);
                                    }
                                }
                            }
                        }
                    }

                    builder.Save(stream, writeOptions, ct);

                    if (stream.Length >= initialSize * 0.99)
                    {
                        throw new FileSkippedException("The PDF is already well optimized.");
                    }
                }
                catch (PdfException ex)
                {
                    throw PdfSupport.Wrap(ex);
                }
            }, ct),
            cancellationToken).ConfigureAwait(false);
    }
}
