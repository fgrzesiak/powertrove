using System.Text;
using FileKit.Html.Testing;

namespace FileKit.Mail.Tests;

public sealed class MimeMailReaderTests
{
    private const string Envelope =
        "From: =?utf-8?B?U2VuZGVyIEdyw7zDn2U=?= <sender@example.invalid>\r\n" +
        "To: Receiver <receiver@example.invalid>\r\n" +
        "Cc: Copy <copy@example.invalid>\r\n" +
        "Subject: =?utf-8?B?UHJpdmF0ZSBHcsO8w59l?=\r\n" +
        "Date: Mon, 26 Oct 2026 10:00:00 +0100\r\n" +
        "MIME-Version: 1.0\r\n";

    private static MimeMailMessage Read(string value, MimeMailLimits? limits = null) =>
        MimeMailReader.Parse(Encoding.Latin1.GetBytes(value), limits);

    [Fact]
    public void ExistingPlainFactoryProducesExpectedUnicodeAndHeaders()
    {
        var message = MimeMailReader.Parse(TestEmlFactory.Plain());
        Assert.Equal("Grüße\nSecond line", message.TextBody);
        Assert.Null(message.HtmlBody);
        Assert.Contains("Sender Grüße", message.From);
        Assert.Equal("Private Grüße", message.Subject);
        Assert.Contains("Receiver", message.To);
        Assert.Contains("Copy", message.Cc);
        Assert.Empty(message.Parts.Where(p => p.IsAttachment));
    }

    [Fact]
    public void ExistingRelatedAndMixedFactoryKeepsImagesAndAttachments()
    {
        byte[] png = [137, 80, 78, 71, 1, 2, 3, 4];
        var message = MimeMailReader.Parse(TestEmlFactory.Html(png));
        Assert.Contains("cid:blue-image", message.HtmlBody);
        var inline = Assert.Single(message.Parts.Where(p => p.ContentId == "blue-image"));
        Assert.Equal("image/png", inline.MediaType);
        Assert.Equal(png, inline.Content.ToArray());
        var attachments = message.Parts.Where(p => p.IsAttachment).ToArray();
        Assert.Equal(2, attachments.Length);
        Assert.All(attachments, part =>
        {
            Assert.Equal("../CON.txt", part.FileName);
            Assert.StartsWith("attachment", Encoding.UTF8.GetString(part.Content.Span));
        });
    }

    [Fact]
    public void AlternativePrefersHtmlAndPreservesPlainFallback()
    {
        var mail = Envelope + "Content-Type: multipart/alternative; boundary=\"al\"\r\n\r\n" +
            "--al\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nFallback\r\n" +
            "--al\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<b>Rich</b>\r\n--al--\r\n";
        var parsed = Read(mail);
        Assert.Equal("Fallback", parsed.TextBody);
        Assert.Equal("<b>Rich</b>", parsed.HtmlBody);
    }

    [Fact]
    public void FoldedHeadersRfc2047AndRfc2231ContinuationsAreDecoded()
    {
        const string mail = "From: Sender <sender@example.invalid>\r\n" +
            "Date: Mon, 26 Oct 2026 10:00:00 +0100\r\n" +
            "Subject: =?windows-1252?Q?Caf=E9?=\r\n" +
            " =?utf-8?B?IGjDqXJv?=\r\n" +
            "Content-Type: multipart/mixed; boundary=m\r\n\r\n" +
            "--m\r\nContent-Type: text/plain; charset=iso-8859-1\r\n" +
            "Content-Transfer-Encoding: quoted-printable\r\n\r\n" +
            "Ol=E1=20mundo=21\r\n" +
            "--m\r\nContent-Type: application/octet-stream\r\n" +
            "Content-Disposition: attachment;\r\n filename*0*=utf-8''R%C3%A9;\r\n filename*1*=sum%C3%A9.txt\r\n" +
            "Content-Transfer-Encoding: base64\r\n\r\n" +
            "YQ==\r\n--m--\r\n";
        var parsed = Read(mail);
        Assert.Equal("Café héro", parsed.Subject);
        Assert.Equal("Olá mundo!", parsed.TextBody);
        var attachment = Assert.Single(parsed.Parts.Where(p => p.IsAttachment));
        Assert.Equal("Résumé.txt", attachment.FileName);
        Assert.Equal("a", Encoding.ASCII.GetString(attachment.Content.Span));
    }

    [Fact]
    public void Rfc2231MixedExtendedAndLiteralContinuationSegments()
    {
        const string mail = Envelope + "Content-Type: multipart/mixed; boundary=b\r\n\r\n" +
            "--b\r\nContent-Type: text/plain\r\n\r\nHi\r\n" +
            "--b\r\nContent-Type: application/octet-stream\r\n" +
            "Content-Disposition: attachment; filename*0*=utf-8''R%C3%A9;" +
            " filename*1=su; filename*2*=m%C3%A9.txt\r\n\r\nx\r\n--b--\r\n";
        var parsed = Read(mail);
        Assert.Equal("Résumé.txt", Assert.Single(parsed.Parts.Where(p => p.IsAttachment)).FileName);
    }

    [Fact]
    public void Windows1252AndEightBitBodyAreSupported()
    {
        string headers = Envelope +
            "Content-Type: text/plain; charset=windows-1252\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n\r\n";
        byte[] data = [.. Encoding.ASCII.GetBytes(headers), 0x95, 0x20, 0x43, 0x61, 0x66, 0xe9];
        var parsed = MimeMailReader.Parse(data);
        Assert.Equal("• Café", parsed.TextBody);
    }

    [Theory]
    [InlineData("base64", "SGVsbG8=")]
    [InlineData("quoted-printable", "Hello=3D\r\n")]
    [InlineData("7bit", "Hello")]
    [InlineData("8bit", "Hello")]
    public void SupportedTransferEncodingsDoNotChangePrintableContent(string encoding, string payload)
    {
        var parsed = Read(Envelope +
            $"Content-Type: text/plain; charset=us-ascii\r\nContent-Transfer-Encoding: {encoding}\r\n\r\n" +
            payload);
        Assert.StartsWith("Hello", parsed.TextBody);
    }

    [Fact]
    public void NestedMultipartPartsAndDepthAreBounded()
    {
        string body = "Content-Type: text/plain\r\n\r\nx";
        for (int i = 0; i < 34; i++)
        {
            string boundary = "nested" + i;
            body = "Content-Type: multipart/mixed; boundary=" + boundary + "\r\n\r\n" +
                   "--" + boundary + "\r\n" + body + "\r\n--" + boundary + "--\r\n";
        }
        Assert.Throws<MimeMailException>(() => Read(Envelope + body));
    }

    [Fact]
    public void AttachmentLimitsAndMalformedMimesAreRejected()
    {
        string big = new('A', 1024);
        var mail = Envelope +
            "Content-Type: multipart/mixed; boundary=b\r\n\r\n" +
            "--b\r\nContent-Type: text/plain\r\n\r\nBody\r\n" +
            "--b\r\nContent-Type: application/octet-stream\r\n" +
            "Content-Disposition: attachment; filename=big.bin\r\n\r\n" + big + "\r\n--b--\r\n";
        Assert.Throws<MimeMailException>(() => Read(mail, new MimeMailLimits { MaxAttachmentBytes = 100 }));
        Assert.Throws<MimeMailException>(() => Read(Envelope +
            "Content-Type: multipart/mixed; boundary=b\r\n\r\nNo delimiter at all"));
    }

    [Fact]
    public void CountDecodedBytesAndInlineImageLimitsAreEnforced()
    {
        byte[] message = TestEmlFactory.Html([1, 2, 3, 4, 5], attachments: true);
        Assert.Throws<MimeMailException>(() =>
            MimeMailReader.Parse(message, new MimeMailLimits { MaxParts = 2 }));
        Assert.Throws<MimeMailException>(() =>
            MimeMailReader.Parse(message, new MimeMailLimits { MaxAttachmentCount = 1 }));
        Assert.Throws<MimeMailException>(() =>
            MimeMailReader.Parse(message, new MimeMailLimits { MaxInlineImageBytes = 4 }));
        Assert.Throws<MimeMailException>(() =>
            MimeMailReader.Parse(message, new MimeMailLimits { MaxTotalDecodedBytes = 30 }));
    }

    [Fact]
    public void InvalidEncodedContentAndDuplicateStructureHeadersFail()
    {
        Assert.Throws<MimeMailException>(() => Read(Envelope +
            "Content-Type: text/plain\r\nContent-Transfer-Encoding: base64\r\n\r\n%%"));
        Assert.Throws<MimeMailException>(() => Read(Envelope +
            "Content-Type: text/plain\r\nContent-Type: text/html\r\n\r\nx"));
        Assert.Throws<MimeMailException>(() => Read(Envelope +
            "Content-Type: text/plain\r\nContent-Transfer-Encoding: 7bit\r\n\r\n" +
            "x", new MimeMailLimits { MaxHeaders = 2 }));
    }

    [Fact]
    public void CancellationStopsBeforeParsingAndSizeLimitIsEnforced()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            MimeMailReader.Parse(TestEmlFactory.Plain(), cancellationToken: cancel.Token));
        Assert.Throws<MimeMailException>(() =>
            MimeMailReader.Parse(TestEmlFactory.Plain(),
                new MimeMailLimits { MaxInputBytes = 20 }));
    }

    [Fact]
    public void HtmlPartsCannotTriggerMailboxOrExternalAccess()
    {
        var message = MimeMailReader.Parse(TestEmlFactory.Html(null,
            "<img src=\"https://remote.example.invalid/track.png\">", attachments: false));
        Assert.Contains("https://remote.example.invalid/track.png", message.HtmlBody);
        // The MIME library only parses bytes. The existing adapter's HTML sanitizer
        // strips this external URL before any HTML/PDF rendering.
        Assert.Empty(message.Parts.Where(p => p.IsAttachment));
    }

    [Fact]
    public void AMessageCutOffBeforeItsFinalDelimiterKeepsItsLastPart()
    {
        var message = Read(Envelope + "Content-Type: multipart/mixed; boundary=z\r\n\r\n" +
            "--z \t\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nFirst part\r\n" +
            "--z\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nCut off here\r\n");

        Assert.Equal("First part", message.TextBody);
        Assert.Equal(2, message.Parts.Count(p => p.MediaType == "text/plain"));
    }
}

