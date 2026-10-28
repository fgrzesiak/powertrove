using FileKit.Html;

namespace PowerTrove.Pack.Pdf;

public interface IHtmlPdfRenderer : ILocalHtmlPdfPrinter { }

public sealed class HtmlToPdfAction(FileActionServices services, ILocalHtmlPdfPrinter? renderer) : PerFileAction<HtmlPrintOptions>(services)
{
    public override ActionDescriptor Descriptor => new("powertrove.pdf.html-to-pdf", SR.GetString("HtmlTitle"), SR.GetString("HtmlDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["html", "htm", "pdf", "print", "convert"], true, true,
        Parameters: [ActionParameterDefinition.Choice("Paper", SR.GetString("HtmlPaper"), "a4", [new("a4", SR.GetString("HtmlPaperA4")), new("letter", SR.GetString("HtmlPaperLetter"))]),
            ActionParameterDefinition.Boolean("Landscape", SR.GetString("HtmlLandscape"), false), ActionParameterDefinition.Integer("MarginMm", SR.GetString("HtmlMargin"), 10, 0, 50)],
        Selection: SelectionRequirements.Any("other"));
    protected override FileInputFilter Input => new(SR.GetString("HtmlFiles"), [".html", ".htm"], null);
    protected override string? UnavailableReason => renderer is { IsAvailable: true } ? null : SR.GetString("HtmlUnavailable");
    protected override HtmlPrintOptions ReadOptions(ActionParameterValues values) => new(values.GetChoice("Paper"), values.GetBoolean("Landscape"), values.GetInt32("MarginMm"));
    protected override string DescribeOperation(int count, HtmlPrintOptions options) => SR.GetString("HtmlDescription");
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> files, HtmlPrintOptions options) => [SR.GetString("HtmlLimits")];
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, HtmlPrintOptions options) { yield return new(Path.GetFileNameWithoutExtension(file.Name), ".pdf"); }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false); if (preview.IsBlocked) return preview;
        try
        {
            foreach (var file in ((FileSelection)context.Content).Files.Where(Input.Accepts))
            { await using var input = Services.FileSystem.OpenRead(file.Path!); await renderer!.PrepareAsync(input, ct).ConfigureAwait(false); }
            return preview;
        }
        catch (HtmlPrintException) { return preview with { IsBlocked = true, PreviewText = SR.GetString("HtmlInvalid") }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return preview with { IsBlocked = true, PreviewText = SR.GetString("HtmlReadError") }; }
    }
    protected override async Task ProcessAsync(FileWorkItem item, HtmlPrintOptions options, FileOutputWriter output, CancellationToken ct)
    {
        try
        {
            await using var input = Services.FileSystem.OpenRead(item.Path);
            var document = await renderer!.PrepareAsync(input, ct).ConfigureAwait(false);
            await output.WriteFileFromPathAsync(item.Directory, item.BaseName, ".pdf", (destination, token) => renderer!.RenderAsync(document, destination, options, token), ct).ConfigureAwait(false);
        }
        catch (HtmlPrintException ex) { throw new FileProcessingException(SR.GetString(ex.Failure == HtmlPrintFailure.Cleanup ? "HtmlCleanupFailed" : "HtmlRenderFailed"), ex); }
    }
}
