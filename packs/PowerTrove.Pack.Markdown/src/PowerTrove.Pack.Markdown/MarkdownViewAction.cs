using System.Globalization;

namespace PowerTrove.Pack.Markdown;

public sealed record MarkdownViewOptions;

/// <summary>
/// Opens one Markdown file in the host's document viewer (or its editor). The action itself never writes:
/// it answers <see cref="DocumentPreviewRequest"/>s with rendered, sanitized HTML of the file or of the
/// editor's unsaved text. Saving is a separate, explicit step of the host's editor.
/// </summary>
public sealed class MarkdownViewAction : PerFileAction<MarkdownViewOptions>
{
    public const string ViewId = "powertrove.markdown.view";
    public const string EditId = "powertrove.markdown.edit";
    /// <summary>Largest file the viewer renders; the editor additionally keeps unsaved text below the draft limit.</summary>
    public const long MaximumViewBytes = 2L * 1024 * 1024;
    private readonly IMarkdownEngine? _engine;
    private readonly bool _edit;

    public MarkdownViewAction(FileActionServices services, IMarkdownEngine? engine, bool edit) : base(services)
    {
        _engine = engine; _edit = edit;
        string kind = edit ? "Edit" : "View";
        Descriptor = new(edit ? EditId : ViewId, SR.Get(kind + "Title"), SR.Get(kind + "Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            edit ? ["markdown", "md", "edit", "editor", "write", "readme", "preview", "split"] : ["markdown", "md", "view", "viewer", "preview", "open", "read", "readme"],
            RequiresPreview: edit, ChangesFiles: edit, Selection: SelectionRequirements.Single("text", "other"));
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => new(SR.Get("MarkdownFiles"), MarkdownDocuments.MarkdownExtensions, null);
    protected override int MaximumInputs => 1;
    protected override string? UnavailableReason => _engine is null ? SR.Get("EngineUnavailable") : null;
    protected override MarkdownViewOptions ReadOptions(ActionParameterValues values) => new();
    protected override string DescribeOperation(int inputCount, MarkdownViewOptions options) => SR.Get(_edit ? "EditOperation" : "ViewOperation");
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, MarkdownViewOptions options) => [];

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        var preview = await base.PreviewAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (preview.IsBlocked || !DocumentPreviewRequest.IsRequested(parameters)) return preview;
        var file = ((FileSelection)context.Content).Files.First(Input.Accepts);
        var documents = new MarkdownDocuments(Services.FileSystem, _engine!);
        try
        {
            string markdown = DocumentPreviewRequest.Draft(parameters)
                ?? await documents.ReadTextAsync(file.Path!, MaximumViewBytes, cancellationToken).ConfigureAwait(false);
            var built = await documents.BuildAsync(markdown, file.Path!, ImageMode.Preview, 0, cancellationToken).ConfigureAwait(false);
            var warnings = new List<string>(preview.Warnings);
            if (built.RemovedContent > 0) warnings.Add(string.Format(CultureInfo.CurrentCulture, SR.Get("RemovedContent"), built.RemovedContent));
            if (built.Images.Remote > 0) warnings.Add(string.Format(CultureInfo.CurrentCulture, SR.Get("RemoteImagesBlocked"), built.Images.Remote));
            if (built.Images.Unsupported > 0) warnings.Add(string.Format(CultureInfo.CurrentCulture, SR.Get("UnsupportedImages"), built.Images.Unsupported));
            return preview with { Warnings = warnings, DocumentPreview = new DocumentPreviewData(MarkdownDocuments.PreviewDocument(built), built.Title) };
        }
        catch (FileProcessingException ex) { return preview with { IsBlocked = true, PreviewText = ex.Message }; }
        catch (MarkdownLimitException) { return preview with { IsBlocked = true, PreviewText = SR.Get("PreviewTooLarge") }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return preview with { IsBlocked = true, PreviewText = SR.Get("ReadError") }; }
    }

    protected override Task ProcessAsync(FileWorkItem item, MarkdownViewOptions options, FileOutputWriter output, CancellationToken cancellationToken) =>
        throw new FileProcessingException(SR.Get("ViewerOnly"));
}
