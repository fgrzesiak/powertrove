using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf;

public sealed record ProtectPdfOptions(string Password, string ConfirmPassword,
    bool AllowPrinting, bool AllowCopying);

/// <summary>Protects new copies with the PDF Standard Security Handler /R 6 (AES-256).</summary>
public sealed class ProtectPdfAction : PerFileAction<ProtectPdfOptions>
{
    public ProtectPdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.protect", SR.GetString("ProtectPdf_Title"), SR.GetString("ProtectPdf_Description"),
            new HashSet<ContentKind> { ContentKind.Files }, ["pdf", "password", "encrypt", "protect", "aes256"],
            RequiresPreview: true, ChangesFiles: true,
            Parameters:
            [
                ActionParameterDefinition.Password("Password", SR.GetString("ProtectPdf_Password")),
                ActionParameterDefinition.Password("ConfirmPassword", SR.GetString("ProtectPdf_ConfirmPassword")),
                ActionParameterDefinition.Boolean("AllowPrinting", SR.GetString("ProtectPdf_Print"), true),
                ActionParameterDefinition.Boolean("AllowCopying", SR.GetString("ProtectPdf_Copy"), false)
            ],
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 },
            CanRememberParameters: false);
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override ProtectPdfOptions ReadOptions(ActionParameterValues v) =>
        new(v.GetPassword("Password"), v.GetPassword("ConfirmPassword"),
            v.GetBoolean("AllowPrinting"), v.GetBoolean("AllowCopying"));

    protected override string? ValidateOptions(ProtectPdfOptions o) =>
        string.IsNullOrEmpty(o.Password) ? SR.GetString("ProtectPdf_Empty") :
        !string.Equals(o.Password, o.ConfirmPassword, StringComparison.Ordinal) ?
            SR.GetString("ProtectPdf_Mismatch") : null;

    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, ProtectPdfOptions o)
    {
        yield return SR.GetString("ProtectPdf_Warning");
    }

    protected override string DescribeOperation(int count, ProtectPdfOptions o) =>
        SR.GetString("ProtectPdf_Operation");

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, ProtectPdfOptions o)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (protected)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, ProtectPdfOptions o,
        FileOutputWriter output, CancellationToken token)
    {
        if (ValidateOptions(o) is { } error) throw new FileProcessingException(error);
        var source = await PdfSupport.LoadAsync(item.Path, token).ConfigureAwait(false);
        await output.WriteFileAsync(item.Directory, item.BaseName + " (protected)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() =>
            {
                var builder = new PdfDocumentBuilder();
                for (int index = 0; index < source.PageCount; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    builder.AddPage(source, index);
                }
                builder.Save(stream, new PdfWriteOptions
                {
                    Encryption = new PdfEncryptionOptions(o.Password,
                        Permissions: new PdfPermissions(o.AllowPrinting, o.AllowCopying))
                }, ct);
            }, ct), token).ConfigureAwait(false);
    }
}
