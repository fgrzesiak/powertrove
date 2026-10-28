using FileKit.Html;

namespace PowerTrove.Pack.Pdf;

public sealed class EpubToPdfAction(FileActionServices services, ILocalHtmlPdfPrinter? renderer, IEpubDocumentBuilder? builder) : PerFileAction<HtmlPrintOptions>(services)
{
    public override ActionDescriptor Descriptor => new("powertrove.pdf.epub-to-pdf", SR.GetString("EpubTitle"), SR.GetString("EpubDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["epub", "ebook", "book", "pdf", "convert"], true, true,
        Parameters: [ActionParameterDefinition.Choice("Paper", SR.GetString("HtmlPaper"), "a4", [new("a4", SR.GetString("HtmlPaperA4")), new("letter", SR.GetString("HtmlPaperLetter"))]),
            ActionParameterDefinition.Boolean("Landscape", SR.GetString("HtmlLandscape"), false), ActionParameterDefinition.Integer("MarginMm", SR.GetString("HtmlMargin"), 10, 0, 50)],
        Selection: SelectionRequirements.Any("other"));
    protected override FileInputFilter Input => new(SR.GetString("EpubFiles"), [".epub"], null);
    protected override string? UnavailableReason => renderer is { IsAvailable: true } && builder is not null ? null : SR.GetString("HtmlUnavailable");
    protected override HtmlPrintOptions ReadOptions(ActionParameterValues values) => new(values.GetChoice("Paper"), values.GetBoolean("Landscape"), values.GetInt32("MarginMm"));
    protected override string DescribeOperation(int count, HtmlPrintOptions options) => SR.GetString("EpubDescription");
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> files, HtmlPrintOptions options) => [SR.GetString("EpubLimits")];
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, HtmlPrintOptions options) { yield return new(Path.GetFileNameWithoutExtension(file.Name), ".pdf"); }
    private async Task<HtmlPrintDocument> PrepareAsync(string path, CancellationToken ct)
    {
        await using var input = Services.FileSystem.OpenRead(path);
        var book = await EpubBook.ReadAsync(input, ct).ConfigureAwait(false);
        return await Task.Run(() => builder!.Build(book, ct), ct).ConfigureAwait(false);
    }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false); if (preview.IsBlocked) return preview;
        try
        {
            foreach (var file in ((FileSelection)context.Content).Files.Where(Input.Accepts)) await PrepareAsync(file.Path!, ct).ConfigureAwait(false);
            return preview;
        }
        catch (EpubException ex) { return preview with { IsBlocked = true, PreviewText = Message(ex.Failure) }; }
        catch (HtmlPrintException) { return preview with { IsBlocked = true, PreviewText = SR.GetString("EpubUnsupported") }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return preview with { IsBlocked = true, PreviewText = SR.GetString("HtmlReadError") }; }
    }
    protected override async Task ProcessAsync(FileWorkItem item, HtmlPrintOptions options, FileOutputWriter output, CancellationToken ct)
    {
        try
        {
            var document = await PrepareAsync(item.Path, ct).ConfigureAwait(false);
            await output.WriteFileFromPathAsync(item.Directory, item.BaseName, ".pdf", (destination, token) => renderer!.RenderAsync(document, destination, options, token), ct).ConfigureAwait(false);
        }
        catch (EpubException ex) { throw new FileProcessingException(Message(ex.Failure), ex); }
        catch (HtmlPrintException ex) { throw new FileProcessingException(SR.GetString(ex.Failure == HtmlPrintFailure.Cleanup ? "HtmlCleanupFailed" : "EpubRenderFailed"), ex); }
    }
    private static string Message(EpubFailure failure) => SR.GetString(failure switch { EpubFailure.Protected => "EpubProtected", EpubFailure.Limit => "EpubTooLarge", EpubFailure.Unsupported => "EpubUnsupported", _ => "EpubInvalid" });
}
