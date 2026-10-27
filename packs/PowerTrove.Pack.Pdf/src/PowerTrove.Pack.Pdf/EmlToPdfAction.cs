using System.Text;
using FileKit.Html;

namespace PowerTrove.Pack.Pdf;

public sealed record EmailPdfOptions(HtmlPrintOptions Print, bool ExportAttachments);
public sealed class EmlToPdfAction(FileActionServices services, ILocalHtmlPdfPrinter? renderer, IEmailPrintReader? reader) : PerFileAction<EmailPdfOptions>(services)
{
    public override ActionDescriptor Descriptor { get; } = new("powertrove.pdf.eml-to-pdf", SR.GetString("EmlTitle"), SR.GetString("EmlDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["eml", "email", "mail", "pdf", "convert"], true, true,
        Parameters: [ActionParameterDefinition.Choice("Paper", SR.GetString("HtmlPaper"), "a4", [new("a4", SR.GetString("HtmlPaperA4")), new("letter", SR.GetString("HtmlPaperLetter"))]),
            ActionParameterDefinition.Boolean("Landscape", SR.GetString("HtmlLandscape"), false), ActionParameterDefinition.Integer("MarginMm", SR.GetString("HtmlMargin"), 10, 0, 50),
            ActionParameterDefinition.Boolean("ExportAttachments", SR.GetString("EmlExportAttachments"), false)],
        Selection: SelectionRequirements.Any("other"));
    protected override FileInputFilter Input => new(SR.GetString("EmlFiles"), [".eml"], null);
    protected override string? UnavailableReason => renderer is { IsAvailable: true } && reader is not null ? null : SR.GetString("HtmlUnavailable");
    protected override EmailPdfOptions ReadOptions(ActionParameterValues values) => new(new(values.GetChoice("Paper"), values.GetBoolean("Landscape"), values.GetInt32("MarginMm")), values.GetBoolean("ExportAttachments"));
    protected override string DescribeOperation(int count, EmailPdfOptions options) => SR.GetString("EmlDescription");
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> files, EmailPdfOptions options) => [SR.GetString("EmlLimits")];
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, EmailPdfOptions options)
    { yield return options.ExportAttachments ? new(Path.GetFileNameWithoutExtension(file.Name) + "-mail", "", true) : new(Path.GetFileNameWithoutExtension(file.Name), ".pdf"); }
    private static EmailPrintLabels Labels() => new(SR.GetString("EmlFrom"), SR.GetString("EmlTo"), SR.GetString("EmlCc"), SR.GetString("EmlSubject"), SR.GetString("EmlDate"), SR.GetString("EmlAttachments"), SR.GetString("EmlPrintNotice"));
    private async Task<(EmailPrintDocument Mail, HtmlPrintDocument Print)> PrepareAsync(string path, bool attachments, CancellationToken ct)
    {
        await using var source = Services.FileSystem.OpenRead(path);
        var mail = await reader!.ReadAsync(source, Labels(), attachments, ct).ConfigureAwait(false);
        using var html = new MemoryStream(Encoding.UTF8.GetBytes(mail.Html));
        return (mail, await renderer!.PrepareAsync(html, ct).ConfigureAwait(false));
    }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false); if (preview.IsBlocked) return preview;
        try
        {
            bool attachments = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters)).ExportAttachments;
            foreach (var file in ((FileSelection)context.Content).Files.Where(Input.Accepts)) await PrepareAsync(file.Path!, attachments, ct).ConfigureAwait(false);
            return preview;
        }
        catch (Exception ex) when (ex is EmailPrintException or HtmlPrintException) { return preview with { IsBlocked = true, PreviewText = SR.GetString("EmlInvalid") }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return preview with { IsBlocked = true, PreviewText = SR.GetString("EmlReadError") }; }
    }
    protected override async Task ProcessAsync(FileWorkItem item, EmailPdfOptions options, FileOutputWriter output, CancellationToken ct)
    {
        try
        {
            var document = await PrepareAsync(item.Path, options.ExportAttachments, ct).ConfigureAwait(false);
            if (!options.ExportAttachments)
                await output.WriteFileFromPathAsync(item.Directory, item.BaseName, ".pdf", (path, token) => renderer!.RenderAsync(document.Print, path, options.Print, token), ct).ConfigureAwait(false);
            else
                await output.WriteDirectoryAsync(item.Directory, item.BaseName + "-mail", async (directory, token) =>
                {
                    await renderer!.RenderAsync(document.Print, Path.Combine(directory, "message.pdf"), options.Print, token).ConfigureAwait(false);
                    string attachments = Path.Combine(directory, "attachments"); Services.FileSystem.CreateDirectory(attachments); int index = 0;
                    foreach (var attachment in document.Mail.Attachments)
                    {
                        token.ThrowIfCancellationRequested();
                        await using var stream = Services.FileSystem.CreateFile(Path.Combine(attachments, EmailAttachmentNames.ForExport(attachment.FileName, ++index)));
                        await stream.WriteAsync(attachment.Data, token).ConfigureAwait(false);
                    }
                }, ct).ConfigureAwait(false);
        }
        catch (EmailPrintException ex) { throw new FileProcessingException(SR.GetString("EmlInvalid"), ex); }
        catch (HtmlPrintException ex) { throw new FileProcessingException(SR.GetString(ex.Failure == HtmlPrintFailure.Cleanup ? "HtmlCleanupFailed" : "EmlRenderFailed"), ex); }
    }
}
