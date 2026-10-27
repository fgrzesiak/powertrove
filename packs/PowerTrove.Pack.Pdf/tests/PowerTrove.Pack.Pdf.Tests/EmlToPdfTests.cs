using FileKit.Html;
using FileKit.Html.Testing;
using PowerTrove.Pack.Pdf.Html;
using PowerTrove.Pack.Pdf.Mail;

namespace PowerTrove.Pack.Pdf.Tests;

public sealed class EmlToPdfTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    private static EmailPrintLabels Labels => new("From", "To", "Cc", "Subject", "Date", "Attachments", "Static email; active content omitted; no signature validation.");
    [Fact]
    public async Task PlainMimePreservesDecodedHeadersDatesUnicodeAndLineBreaks()
    {
        using var input = new MemoryStream(TestEmlFactory.Plain("Grüße\n<not markup>")); var mail = await new EmlPrintReader().ReadAsync(input, Labels, false, default);
        Assert.Contains("Private Gr&#252;&#223;e", mail.Html); Assert.Contains("sender@example.invalid", mail.Html); Assert.Contains("receiver@example.invalid", mail.Html); Assert.Contains("copy@example.invalid", mail.Html);
        Assert.Contains("26 Oct 2026", mail.Html); Assert.Contains("&lt;not markup&gt;", mail.Html); Assert.Contains("\n", mail.Html);
        Assert.Equal(0, mail.AttachmentCount); Assert.Empty(mail.Attachments);
        HtmlPrintDocument.FromText(mail.Html, new SafeHtmlMarkupValidator());
    }
    [Fact]
    public async Task HtmlCidImagesAndRegularAttachmentsAreBoundedAndActiveRemoteContentRemoved()
    {
        using var input = new MemoryStream(TestEmlFactory.Html([1, 2, 3], "<script>fetch('https://example.invalid')</script><img src='https://example.invalid/track'><a href='file:///private'>link</a><p onclick='active()'>text</p><iframe srcdoc='active'></iframe>"));
        var mail = await new EmlPrintReader().ReadAsync(input, Labels, true, default);
        Assert.Contains("data:image/png;base64,AQID", mail.Html); Assert.DoesNotContain("https://example.invalid", mail.Html); Assert.DoesNotContain("file:///", mail.Html); Assert.DoesNotContain("<script", mail.Html); Assert.DoesNotContain("onclick", mail.Html); Assert.DoesNotContain("<iframe", mail.Html);
        Assert.Contains("@scope (.mail-body)", mail.Html); Assert.Equal(2, mail.AttachmentCount); Assert.Equal(2, mail.Attachments.Count);
        Assert.Equal("attachment 0", System.Text.Encoding.UTF8.GetString(mail.Attachments[0].Data.Span));
        Assert.Equal("0001-CON.txt", EmailAttachmentNames.ForExport(mail.Attachments[0].FileName, 1));
        Assert.DoesNotContain("..", EmailAttachmentNames.ForExport("../../.\\private.txt", 2));
        HtmlPrintDocument.FromText(mail.Html, new SafeHtmlMarkupValidator());
    }
    [Theory]
    [InlineData("malformed")]
    [InlineData("From: sender@example.invalid\r\nDate: Mon, 26 Oct 2026 10:00:00 +0100\r\nContent-Type: multipart/encrypted; boundary=x; protocol=application/pgp-encrypted\r\n\r\n--x\r\nContent-Type: application/pgp-encrypted\r\n\r\nVersion: 1\r\n--x--")]
    public async Task InvalidOrEncryptedMailHasNoPrintableFallback(string source)
    {
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(source)); await Assert.ThrowsAsync<EmailPrintException>(() => new EmlPrintReader().ReadAsync(input, Labels, false, default));
    }
    [Fact]
    public async Task InputAttachmentStructureAndCancellationLimitsFailBeforePublishingAnything()
    {
        var reader = new EmlPrintReader();
        using var oversized = new MemoryStream(new byte[EmlPrintReader.MaxMailBytes + 1]);
        await Assert.ThrowsAsync<EmailPrintException>(() => reader.ReadAsync(oversized, Labels, false, default));
        using var cancelled = new MemoryStream(TestEmlFactory.Plain());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancelled, Labels, true, new(true)));
        string message = System.Text.Encoding.ASCII.GetString(TestEmlFactory.Html());
        string attachment = "--outer\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=test.bin\r\nContent-Transfer-Encoding: base64\r\n\r\nAQID\r\n";
        using var tooMany = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(message.Replace("--outer--", string.Concat(Enumerable.Repeat(attachment, 101)) + "--outer--", StringComparison.Ordinal)));
        await Assert.ThrowsAsync<EmailPrintException>(() => reader.ReadAsync(tooMany, Labels, true, default));
        using var delimiterBomb = new MemoryStream(TestEmlFactory.Plain(string.Concat(Enumerable.Repeat("--line\n", 2300))));
        // Base64 is plain text, not a MIME delimiter sequence; valid text remains readable.
        Assert.Contains("--line", (await reader.ReadAsync(delimiterBomb, Labels, false, default)).Html);
    }
    [Fact]
    public async Task RegisteredActionExportsWholeFoldersAtomicallyAndPreservesSourcesAndExistingOutputs()
    {
        var renderer = new Renderer(); var action = new PdfPack(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()), htmlRenderer: renderer, emailReader: new EmlPrintReader()).Actions.Single(a => a.Descriptor.Id == "powertrove.pdf.eml-to-pdf");
        byte[] source = TestEmlFactory.Html(); string one = _directory.Write("one.eml", source), two = _directory.Write("two.eml", TestEmlFactory.Plain());
        Directory.CreateDirectory(_directory.File("one-mail")); _directory.Write("one.pdf", "existing");
        var context = Snapshots.ForPaths(one, two); var options = Snapshots.Parameters(("Paper", "letter"), ("ExportAttachments", "true"));
        Assert.True((await action.MatchAsync(context, default)).IsAvailable); var preview = await action.PreviewAsync(context, options, default); Assert.False(preview.IsBlocked); Assert.DoesNotContain("Private", preview.PreviewText);
        var result = await action.ExecuteAsync(context, options, null, default); Assert.True(result.IsSuccess, result.Message); Assert.Equal(2, result.OutputPaths.Count);
        string folder = result.OutputPaths.Single(p => Directory.GetFiles(Path.Combine(p, "attachments")).Length == 2);
        Assert.True(File.Exists(Path.Combine(folder, "message.pdf"))); Assert.Equal(2, Directory.GetFiles(Path.Combine(folder, "attachments")).Length); Assert.All(Directory.GetFiles(Path.Combine(folder, "attachments")), p => Assert.Equal(Path.Combine(folder, "attachments"), Path.GetDirectoryName(p)));
        Assert.Equal(source, File.ReadAllBytes(one)); Assert.Equal("existing", File.ReadAllText(_directory.File("one.pdf"))); Assert.Equal(new HtmlPrintOptions("letter"), renderer.Options);
        renderer.Fail = true; string failed = _directory.Write("failed.eml", source);
        Assert.False((await action.ExecuteAsync(Snapshots.ForPaths(failed), options, null, default)).IsSuccess); Assert.False(Directory.Exists(_directory.File("failed-mail"))); Assert.Empty(Directory.GetDirectories(_directory.Path, ".~cb*"));
    }
    private sealed class Renderer : IHtmlPdfRenderer
    {
        public bool IsAvailable => true; public bool Fail { get; set; } public HtmlPrintOptions? Options { get; private set; }
        public Task<HtmlPrintDocument> PrepareAsync(Stream source, CancellationToken ct) => HtmlPrintDocument.ReadAsync(source, new SafeHtmlMarkupValidator(), ct);
        public async Task RenderAsync(HtmlPrintDocument document, string path, HtmlPrintOptions options, CancellationToken ct)
        { Options = options; await File.WriteAllTextAsync(path, "%PDF-1.4\ncontract-test", ct); if (Fail) throw new HtmlPrintException("failed print"); }
    }
}
