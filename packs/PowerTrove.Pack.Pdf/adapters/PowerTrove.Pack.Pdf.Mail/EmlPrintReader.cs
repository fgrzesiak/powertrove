using System.Globalization;
using System.Net;
using System.Text;
using AngleSharp.Html.Parser;
using FileKit.Html;
using FileKit.Mail;

namespace PowerTrove.Pack.Pdf.Mail;

/// <summary>Local MIME parsing only. Never connects to a mailbox or renders active mail.</summary>
public sealed class EmlPrintReader : IEmailPrintReader
{
    public const int MaxMailBytes = 25 * 1024 * 1024;

    public async Task<EmailPrintDocument> ReadAsync(Stream source, EmailPrintLabels labels, bool exportAttachments, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + count > MaxMailBytes) throw new EmailPrintException("EML input exceeds 25 MiB.");
            await memory.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        return await Task.Run(() => Parse(memory.ToArray(), labels, exportAttachments, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static EmailPrintDocument Parse(byte[] input, EmailPrintLabels labels, bool exportAttachments, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            MimeMailMessage message = MimeMailReader.Parse(input, new MimeMailLimits
            {
                MaxInputBytes = MaxMailBytes,
                MaxDepth = 32,
                MaxParts = 1000,
                MaxHeaders = 256,
                MaxHeaderValueBytes = 32768,
                MaxDelimiters = 2200,
                MaxAttachmentCount = 100,
                MaxAttachmentBytes = 10 * 1024 * 1024,
                MaxInlineImageBytes = 2 * 1024 * 1024,
                MaxBodyBytes = HtmlPrintDocument.MaxBytes,
                MaxTotalDecodedBytes = MaxMailBytes
            }, ct);

            var attachments = new List<EmailAttachment>();
            var images = new Dictionary<string, string>(StringComparer.Ordinal);
            int attachmentCount = 0;
            foreach (var part in message.Parts)
            {
                ct.ThrowIfCancellationRequested();
                bool inlineImage = IsImage(part.MediaType) && part.ContentId is not null;
                if (part.IsAttachment)
                {
                    if (++attachmentCount > 100) throw new EmailPrintException("EML attachment limit exceeded.");
                    if (exportAttachments)
                    {
                        string name = part.FileName ?? (part.IsMessageAttachment ? "message.eml" : "attachment.bin");
                        attachments.Add(new EmailAttachment(name, part.Content));
                    }
                }

                if (inlineImage)
                {
                    string cid = part.ContentId!;
                    string encoded = "data:" + part.MediaType.ToLowerInvariant() + ";base64," +
                                     Convert.ToBase64String(part.Content.Span);
                    if (!images.TryAdd(cid, encoded))
                        throw new EmailPrintException("Ambiguous inline mail image identifiers.");
                }
            }

            string body = message.HtmlBody is not null
                ? Sanitize(message.HtmlBody, images, ct)
                : "<pre style='white-space:pre-wrap;overflow-wrap:anywhere'>" +
                  Encode(message.TextBody!) + "</pre>";

            var result = new StringBuilder("<html><head><meta charset='utf-8'><style>body{font:11pt sans-serif;overflow-wrap:anywhere}table{border-collapse:collapse}th{text-align:left;vertical-align:top;padding-right:1em}img{max-width:100%;height:auto}.mail-notice{font-size:9pt;border-block:1px solid #999;padding:.5em 0}</style></head><body><table>");
            Header(labels.From, message.From);
            Header(labels.To, message.To);
            Header(labels.Cc, message.Cc);
            Header(labels.Subject, message.Subject);
            Header(labels.Date, message.Date);
            Header(labels.Attachments, attachmentCount.ToString(CultureInfo.InvariantCulture));
            result.Append("</table><p class='mail-notice'>").Append(Encode(labels.Notice))
                  .Append("</p><article>").Append(body).Append("</article></body></html>");

            string prepared = result.ToString();
            if (Encoding.UTF8.GetByteCount(prepared) > HtmlPrintDocument.MaxBytes)
                throw new EmailPrintException("Prepared mail exceeds 5 MiB.");
            return new EmailPrintDocument(prepared, attachmentCount, attachments.AsReadOnly());

            void Header(string label, string value)
            {
                if (value.Length > 32768) throw new EmailPrintException("Decoded mail header limit exceeded.");
                result.Append("<tr><th>").Append(Encode(label)).Append("</th><td>")
                      .Append(Encode(value)).Append("</td></tr>");
            }
        }
        catch (MimeMailException ex)
        {
            throw new EmailPrintException("The EML message is invalid or unsupported.", ex);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            throw new EmailPrintException("The EML message is invalid or unsupported.", ex);
        }
    }

    private static readonly HashSet<string> Passive = new("html head body title style div p span br hr h1 h2 h3 h4 h5 h6 table thead tbody tfoot tr th td caption colgroup col a img ol ul li dl dt dd pre code blockquote b strong i em u s strike small sub sup center font address section article header footer main nav figure figcaption details summary".Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> Attributes = new("class id style lang dir title alt width height colspan rowspan align valign border cellpadding cellspacing bgcolor color face size".Split(' '), StringComparer.Ordinal);
    private static string Sanitize(string html, Dictionary<string, string> images, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(html) > HtmlPrintDocument.MaxBytes) throw new EmailPrintException("Mail HTML exceeds 5 MiB.");
        int tags = 0;
        using var document = new HtmlParser(new HtmlParserOptions { IsScripting = false, OnToken = (token, _) => { if (token.Type == HtmlTokenType.StartTag && (++tags > 50000 || token.IsStartTag("template"))) throw new EmailPrintException("Unsupported mail HTML structure."); } }).ParseDocument(html);
        foreach (var element in document.All.ToArray())
        {
            ct.ThrowIfCancellationRequested(); int depth = 0; for (var p = element.ParentElement; p is not null; p = p.ParentElement) if (++depth > 128) throw new EmailPrintException("Mail HTML nesting limit exceeded.");
            if (element.NamespaceUri != "http://www.w3.org/1999/xhtml" || !Passive.Contains(element.LocalName)) { element.Remove(); continue; }
            foreach (var attribute in element.Attributes.ToArray()) if (!Attributes.Contains(attribute.Name) && !(element.LocalName == "img" && attribute.Name == "src")) element.RemoveAttribute(attribute.Name);
            if (element.HasAttribute("style")) element.SetAttribute("style", CleanCss(element.GetAttribute("style")!));
            if (element.LocalName == "style") element.TextContent = "@scope (.mail-body){" + CleanCss(element.TextContent) + "}";
            if (element.LocalName == "img")
            {
                string source = element.GetAttribute("src") ?? "";
                if (source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) && images.TryGetValue(Uri.UnescapeDataString(source[4..]), out var data)) element.SetAttribute("src", data);
                else if (!ValidDataImage(source)) element.RemoveAttribute("src");
            }
        }
        var body = document.Body!; var section = document.CreateElement("section"); section.ClassName = "mail-body";
        var content = document.CreateElement("div");
        foreach (var attribute in body.Attributes) content.SetAttribute(attribute.Name, attribute.Value);
        content.InnerHtml = body.InnerHtml; section.AppendChild(content);
        return string.Concat(document.Head!.QuerySelectorAll("style").Select(s => s.OuterHtml)) + section.OuterHtml;
    }
    private static string CleanCss(string css)
    {
        // Reject resource-bearing CSS entirely; independent browser CSP also blocks requests.
        if (css.Length > 1024 * 1024 || css.Contains('\\') || css.Contains('<') || css.Contains('@') || css.Contains("url", StringComparison.OrdinalIgnoreCase) || css.Contains("expression", StringComparison.OrdinalIgnoreCase)) return "";
        return css;
    }
    private static bool IsImage(string type) => type.ToLowerInvariant() is "image/png" or "image/jpeg" or "image/gif" or "image/webp";
    private static bool ValidDataImage(string source)
    {
        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || source.Length > 3 * 1024 * 1024) return false;
        int comma = source.IndexOf(','); if (comma < 0 || !source[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) || !IsImage(source[5..(comma - 7)])) return false;
        try { return Convert.FromBase64String(source[(comma + 1)..]).Length <= 2 * 1024 * 1024; } catch (FormatException) { return false; }
    }
    private static string Encode(string value) => WebUtility.HtmlEncode(value);
    private sealed class LimitedStream(int limit) : Stream
    {
        private readonly MemoryStream _inner = new(); public ReadOnlyMemory<byte> Bytes => _inner.ToArray();
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true; public override long Length => _inner.Length;
        public override long Position { get => Length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { if (Length + count > limit) throw new EmailPrintException("Decoded MIME part limit exceeded."); _inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { if (Length + buffer.Length > limit) throw new EmailPrintException("Decoded MIME part limit exceeded."); _inner.Write(buffer); }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
