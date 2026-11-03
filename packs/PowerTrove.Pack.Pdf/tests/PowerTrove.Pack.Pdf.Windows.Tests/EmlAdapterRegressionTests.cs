using FileKit.Html;
using FileKit.Html.Testing;
using PowerTrove.Pack.Pdf.Mail;

namespace PowerTrove.Pack.Pdf.Windows.Tests;

public sealed class EmlAdapterRegressionTests
{
    private static readonly EmailPrintLabels Labels =
        new("From", "To", "Cc", "Subject", "Date", "Attachments", "Local preview only");

    [Fact]
    public async Task ExistingPlainMailProducesPrintableHeaderAndBody()
    {
        await using var source = new MemoryStream(TestEmlFactory.Plain());
        var output = await new EmlPrintReader().ReadAsync(source, Labels, false, default);
        // The HTML encodes non-ASCII letters as character references.
        string html = System.Net.WebUtility.HtmlDecode(output.Html);
        Assert.Contains("Private Grüße", html);
        Assert.Contains("Sender Grüße", html);
        Assert.Contains("Grüße", html);
        Assert.Contains("Second line", html);
        Assert.Equal(0, output.AttachmentCount);
        Assert.Empty(output.Attachments);
    }

    [Fact]
    public async Task ExistingMixedRelatedMailIncludesCidAndOptionalAttachments()
    {
        byte[] png = [137, 80, 78, 71, 1, 2, 3, 4];
        byte[] sourceBytes = TestEmlFactory.Html(png, extra: "<script>alert('x')</script>");
        var reader = new EmlPrintReader();

        await using var input = new MemoryStream(sourceBytes);
        var result = await reader.ReadAsync(input, Labels, true, default);
        Assert.Equal(2, result.AttachmentCount);
        Assert.Equal(2, result.Attachments.Count);
        Assert.All(result.Attachments, a => Assert.Equal("../CON.txt", a.FileName));
        Assert.Contains("data:image/png;base64,", result.Html);
        Assert.DoesNotContain("<script", result.Html, StringComparison.OrdinalIgnoreCase);

        await using var noExport = new MemoryStream(sourceBytes);
        var metadataOnly = await reader.ReadAsync(noExport, Labels, false, default);
        Assert.Equal(2, metadataOnly.AttachmentCount);
        Assert.Empty(metadataOnly.Attachments);
    }

    [Fact]
    public async Task RemoteMailImagesAndUnsafeElementsRemainBlocked()
    {
        byte[] bytes = TestEmlFactory.Html(null,
            extra: "<img src='https://tracker.example.invalid/x'><iframe src='https://tracker.example.invalid/y'></iframe>",
            attachments: false);
        await using var stream = new MemoryStream(bytes);
        var result = await new EmlPrintReader().ReadAsync(stream, Labels, false, default);
        Assert.DoesNotContain("tracker.example.invalid", result.Html);
        Assert.DoesNotContain("<iframe", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidMailAndCancellationAreRejected()
    {
        await using var malformed = new MemoryStream("Subject: no body\r\n\r\n".Select(ch => (byte)ch).ToArray());
        await Assert.ThrowsAsync<EmailPrintException>(() =>
            new EmlPrintReader().ReadAsync(malformed, Labels, false, default));

        await using var cancelled = new MemoryStream(TestEmlFactory.Plain());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EmlPrintReader().ReadAsync(cancelled, Labels, false, cancellation.Token));
    }
}
