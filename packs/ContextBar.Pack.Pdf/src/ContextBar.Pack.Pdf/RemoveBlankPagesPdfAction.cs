using ContextBar.Abstractions.Files;
using FileKit.Pdf;

namespace ContextBar.Pack.Pdf;

public sealed record RemoveBlankPagesOptions(string Sensitivity);

public sealed class RemoveBlankPagesPdfAction : PerFileAction<RemoveBlankPagesOptions>
{
    private readonly IPdfPageRenderer? _renderer;

    public RemoveBlankPagesPdfAction(FileActionServices services, IPdfPageRenderer? renderer = null) : base(services)
    {
        _renderer = renderer;

        Descriptor = new ActionDescriptor(
            "contextbar.pdf.remove-blank", SR.GetString("RemoveBlankPagesPdfAction_Title"),
            SR.GetString("RemoveBlankPagesPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "remove", "blank", "empty" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("Sensitivity", SR.GetString("RemoveBlankPagesPdfAction_ParamTitle_Sensitivity"), "normal", new[]
                {
                    new ActionParameterChoice("strict", SR.GetString("RemoveBlankPagesPdfAction_ParamChoice_Strict")),
                    new ActionParameterChoice("normal", SR.GetString("RemoveBlankPagesPdfAction_ParamChoice_Normal")),
                    new ActionParameterChoice("relaxed", SR.GetString("RemoveBlankPagesPdfAction_ParamChoice_Relaxed"))
                })
            },
            Selection: SelectionRequirements.Any("pdf"));
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override string? UnavailableReason =>
        (_renderer == null || !_renderer.IsAvailable) ? SR.GetString("Pdf_Error_NoRenderer") : base.UnavailableReason;

    protected override RemoveBlankPagesOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetChoice("Sensitivity"));

    protected override string DescribeOperation(int inputCount, RemoveBlankPagesOptions options) =>
        string.Format(SR.GetString("RemoveBlankPagesPdfAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, RemoveBlankPagesOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (cleaned)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, RemoveBlankPagesOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        if (_renderer == null || !_renderer.IsAvailable)
            throw new FileProcessingException(SR.GetString("Pdf_Error_NoRenderer"));

        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);

        var blankPages = new List<int>();
        for (int i = 0; i < document.PageCount; i++)
        {
            var raster = await _renderer.RenderGrayAsync(item.Path, i, 150, cancellationToken).ConfigureAwait(false);
            if (BlankPageDetector.IsBlank(raster, options.Sensitivity))
            {
                blankPages.Add(i);
            }
        }

        if (blankPages.Count == document.PageCount)
            throw new FileProcessingException("All pages are blank.");

        if (blankPages.Count == 0)
            throw new FileSkippedException("No blank pages found.");

        string directory = Path.GetDirectoryName(item.Path)!;
        string baseName = Path.GetFileNameWithoutExtension(item.File.Name) + " (cleaned)";

        await output.WriteFileAsync(
            directory,
            baseName,
            ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.RemovePages(document, blankPages, stream, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}
