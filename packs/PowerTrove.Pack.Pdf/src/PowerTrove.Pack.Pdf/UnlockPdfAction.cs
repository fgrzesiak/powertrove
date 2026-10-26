using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;


using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf;

public sealed record UnlockPdfOptions(string Password);

public sealed class UnlockPdfAction : PerFileAction<UnlockPdfOptions>
{
    public UnlockPdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.unlock", SR.GetString("UnlockPdfAction_Title"),
            SR.GetString("UnlockPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "unlock", "decrypt", "password" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Password("Password", SR.GetString("UnlockPdfAction_ParamTitle_Password"))
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override UnlockPdfOptions ReadOptions(ActionParameterValues values) => new(values.GetText("Password"));

    protected override string? ValidateOptions(UnlockPdfOptions options) => null;

    protected override string DescribeOperation(int inputCount, UnlockPdfOptions options) =>
        string.Format(SR.GetString("UnlockPdfAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, UnlockPdfOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (unlocked)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, UnlockPdfOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        // Try opening with password
        PdfDocument document;
        try
        {
            document = await PdfSupport.LoadAsync(item.Path, cancellationToken, options.Password).ConfigureAwait(false);
        }
        catch (FileProcessingException ex) when (ex.InnerException is PdfPasswordRequiredException)
        {
            throw new FileProcessingException(SR.GetString("PdfSupport_Error_PasswordRequired"));
        }

        await output.WriteFileAsync(item.Directory, item.BaseName + " (unlocked)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() =>
            {
                var builder = new PdfDocumentBuilder();
                for (int i = 0; i < document.PageCount; i++)
                {
                    builder.AddPage(document, i);
                }
                builder.Save(stream, cancellationToken: ct);
            }, ct),
            cancellationToken).ConfigureAwait(false);
    }
}
