using System.Globalization;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf;

public sealed record FlattenPdfOptions(bool KeepLinks);

/// <summary>Commits supported annotation appearances to normal PDF page content.</summary>
public sealed class FlattenPdfAction : PerFileAction<FlattenPdfOptions>
{
    public FlattenPdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.flatten", SR.GetString("FlattenPdf_Title"), SR.GetString("FlattenPdf_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            ["pdf", "flatten", "forms", "comments", "annotations"],
            RequiresPreview: true, ChangesFiles: true,
            Parameters:
            [
                ActionParameterDefinition.Boolean("KeepLinks", SR.GetString("FlattenPdf_KeepLinks"), true)
            ],
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override FlattenPdfOptions ReadOptions(ActionParameterValues v) =>
        new(v.GetBoolean("KeepLinks"));
    protected override string DescribeOperation(int count, FlattenPdfOptions o) =>
        SR.GetString("FlattenPdf_Operation");

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, FlattenPdfOptions o)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (flattened)", ".pdf");
    }

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters,
        CancellationToken token)
    {
        var preview = await base.PreviewAsync(context, parameters, token).ConfigureAwait(false);
        if (preview.IsBlocked || context.Content is not FileSelection files) return preview;
        var options = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
        var warnings = new List<string>(preview.Warnings) { SR.GetString("FlattenPdf_SignatureWarning") };
        foreach (var file in files.Files.Where(Input.Accepts))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var doc = await PdfSupport.LoadAsync(file.Path!, token).ConfigureAwait(false);
                var summary = await Task.Run(() => PdfAnnotationFlattener.Analyze(doc, options.KeepLinks), token).ConfigureAwait(false);
                warnings.Add(string.Format(CultureInfo.CurrentCulture, SR.GetString("FlattenPdf_Counts"),
                    summary.Flattenable, summary.Discarded, summary.RetainedLinks));
                if (summary.Discarded > 0) warnings.Add(SR.GetString("FlattenPdf_DiscardWarning"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // The regular execution path reports damaged or inaccessible files safely.
            }
        }
        return preview with { Warnings = warnings };
    }

    protected override async Task ProcessAsync(FileWorkItem item, FlattenPdfOptions o,
        FileOutputWriter output, CancellationToken token)
    {
        var source = await PdfSupport.LoadAsync(item.Path, token).ConfigureAwait(false);
        var analysis = await Task.Run(() => PdfAnnotationFlattener.Analyze(source, keepLinks: true), token).ConfigureAwait(false);
        if (analysis.Flattenable == 0 && analysis.Discarded == 0 &&
            (o.KeepLinks || analysis.RetainedLinks == 0))
            throw new FileSkippedException(SR.GetString("FlattenPdf_Nothing"));
        await output.WriteFileAsync(item.Directory, item.BaseName + " (flattened)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.Flatten(source, stream, o.KeepLinks, ct), ct),
            token).ConfigureAwait(false);
    }
}
