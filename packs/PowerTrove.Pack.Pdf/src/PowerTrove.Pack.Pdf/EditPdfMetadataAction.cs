using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf;

public sealed record EditPdfMetadataOptions(string Title, string Author, string Subject, string Keywords);

public sealed class EditPdfMetadataAction : PerFileAction<EditPdfMetadataOptions>
{
    public EditPdfMetadataAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.edit-metadata", SR.GetString("EditPdfMetadataAction_Title"),
            SR.GetString("EditPdfMetadataAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "metadata", "properties", "author", "title", "subject", "keywords" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Text("Title", SR.GetString("EditPdfMetadataAction_ParamTitle_Title"), string.Empty, SR.GetString("EditPdfMetadataAction_ParamDesc_Title")),
                ActionParameterDefinition.Text("Author", SR.GetString("EditPdfMetadataAction_ParamTitle_Author"), string.Empty, SR.GetString("EditPdfMetadataAction_ParamDesc_Author")),
                ActionParameterDefinition.Text("Subject", SR.GetString("EditPdfMetadataAction_ParamTitle_Subject"), string.Empty, SR.GetString("EditPdfMetadataAction_ParamDesc_Subject")),
                ActionParameterDefinition.Text("Keywords", SR.GetString("EditPdfMetadataAction_ParamTitle_Keywords"), string.Empty, SR.GetString("EditPdfMetadataAction_ParamDesc_Keywords"))
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override EditPdfMetadataOptions ReadOptions(ActionParameterValues values) => new(
        values.GetText("Title"),
        values.GetText("Author"),
        values.GetText("Subject"),
        values.GetText("Keywords"));

    protected override string? ValidateOptions(EditPdfMetadataOptions options)
    {
        if (options.Title.Length > 500) return SR.GetString("EditPdfMetadataAction_Error_TooLong");
        if (options.Author.Length > 500) return SR.GetString("EditPdfMetadataAction_Error_TooLong");
        if (options.Subject.Length > 500) return SR.GetString("EditPdfMetadataAction_Error_TooLong");
        if (options.Keywords.Length > 500) return SR.GetString("EditPdfMetadataAction_Error_TooLong");
        return null;
    }

    protected override string DescribeOperation(int inputCount, EditPdfMetadataOptions options) =>
        string.Format(SR.GetString("EditPdfMetadataAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_Copy"), SR.GetString("Pdf_Copies")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, EditPdfMetadataOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (edited)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, EditPdfMetadataOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        await output.WriteFileAsync(item.Directory, item.BaseName + " (edited)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.EditMetadata(document, stream,
                string.IsNullOrEmpty(options.Title) ? null : options.Title,
                string.IsNullOrEmpty(options.Author) ? null : options.Author,
                string.IsNullOrEmpty(options.Subject) ? null : options.Subject,
                string.IsNullOrEmpty(options.Keywords) ? null : options.Keywords, ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}
