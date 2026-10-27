using System.Globalization;
using System.Net;
using System.Text;
using AngleSharp.Html.Parser;
using FileKit.Html;
using MimeKit;

namespace PowerTrove.Pack.Pdf.Mail;

/// <summary>Local MIME parsing only. Never connects to a mailbox or renders active mail.</summary>
public sealed class EmlPrintReader : IEmailPrintReader
{
    public const int MaxMailBytes = 25 * 1024 * 1024;
    public async Task<EmailPrintDocument> ReadAsync(Stream source, EmailPrintLabels labels, bool exportAttachments, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream(); byte[] buffer = new byte[81920]; int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + count > MaxMailBytes) throw new EmailPrintException("EML input exceeds 25 MiB.");
            await memory.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        return await Task.Run(() => Parse(memory, labels, exportAttachments, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
    private static EmailPrintDocument Parse(MemoryStream input, EmailPrintLabels labels, bool exportAttachments, CancellationToken ct)
    {
        try
        {
            input.Position = 0;
            int delimiters = 0;
            var raw = input.GetBuffer().AsSpan(0, (int)input.Length);
            for (int i = 0; i + 2 < raw.Length; i++)
                if ((i == 0 || raw[i - 1] == '\n') && raw[i] == '-' && raw[i + 1] == '-' && ++delimiters > 2200)
                    throw new EmailPrintException("EML MIME delimiter limit exceeded.");
            using var message = MimeMessage.Load(new ParserOptions { MaxMimeDepth = 32, MaxAddressGroupDepth = 3, AddressParserComplianceMode = RfcComplianceMode.Strict, ParameterComplianceMode = RfcComplianceMode.Strict, Rfc2047ComplianceMode = RfcComplianceMode.Strict }, input, ct);
            if (message.Body is null || message.Headers.Count > 256 || message.Headers.Any(h => h.RawValue.Length > 32768) || message.From.Count == 0 || !MimeKit.Utils.DateUtils.TryParse(message.Headers[HeaderId.Date], out _)) throw new EmailPrintException("EML requires bounded valid headers, sender, date and a body.");
            var leaves = new List<MimeEntity>(); int parts = 0;
            Visit(message.Body, 0);
            var attachments = new List<EmailAttachment>(); var images = new Dictionary<string, string>(StringComparer.Ordinal);
            int attachmentCount = 0; long decoded = 0;
            foreach (var entity in leaves)
            {
                ct.ThrowIfCancellationRequested();
                if (entity is MimePart { Content: null } || entity is MessagePart { Message: null }) throw new EmailPrintException("A MIME part has no content.");
                if (entity.ContentType.MimeType is "application/pkcs7-mime" or "application/x-pkcs7-mime" or "application/pgp-encrypted" || entity.ContentType.MediaType.Equals("multipart", StringComparison.OrdinalIgnoreCase)) throw new EmailPrintException("Encrypted or excessively nested MIME is unsupported.");
                bool inlineImage = entity is MimePart image && IsImage(image.ContentType.MimeType) && !string.IsNullOrWhiteSpace(entity.ContentId);
                if (entity.IsAttachment || entity is MessagePart || entity is MimePart partWithName && !string.IsNullOrWhiteSpace(partWithName.FileName) && !inlineImage)
                {
                    if (++attachmentCount > 100) throw new EmailPrintException("EML attachment limit exceeded.");
                    if (exportAttachments)
                    {
                        using var content = new LimitedStream(10 * 1024 * 1024);
                        if (entity is MessagePart { Message: { } nestedMessage }) nestedMessage.WriteTo(content, ct);
                        else if (entity is MimePart { Content: { } attachmentContent }) attachmentContent.DecodeTo(content, ct);
                        else throw new EmailPrintException("Unsupported attachment content.");
                        if ((decoded += content.Length) > MaxMailBytes) throw new EmailPrintException("Decoded mail resources exceed 25 MiB.");
                        string name = entity is MimePart named ? named.FileName ?? "attachment.bin" : "message.eml";
                        attachments.Add(new(name, content.Bytes));
                    }
                }
                if (inlineImage)
                {
                    var imageContent = ((MimePart)entity).Content ?? throw new EmailPrintException("Missing inline image data.");
                    string cid = entity.ContentId ?? throw new EmailPrintException("Missing inline image identifier.");
                    using var content = new LimitedStream(2 * 1024 * 1024); imageContent.DecodeTo(content, ct);
                    if ((decoded += content.Length) > MaxMailBytes || !images.TryAdd(cid, "data:" + entity.ContentType.MimeType.ToLowerInvariant() + ";base64," + Convert.ToBase64String(content.Bytes.Span))) throw new EmailPrintException("Ambiguous or oversized inline mail images.");
                }
            }
            string? html = message.HtmlBody; string? plain = message.TextBody;
            if (html is null && plain is null) throw new EmailPrintException("No supported plain-text or HTML mail body is available.");
            string body = html is not null ? Sanitize(html, images, ct) : "<pre style='white-space:pre-wrap;overflow-wrap:anywhere'>" + Encode(plain!) + "</pre>";
            var result = new StringBuilder("<html><head><meta charset='utf-8'><style>body{font:11pt sans-serif;overflow-wrap:anywhere}table{border-collapse:collapse}th{text-align:left;vertical-align:top;padding-right:1em}img{max-width:100%;height:auto}.mail-notice{font-size:9pt;border-block:1px solid #999;padding:.5em 0}</style></head><body><table>");
            Header(labels.From, message.From.ToString()); Header(labels.To, message.To.ToString()); Header(labels.Cc, message.Cc.ToString()); Header(labels.Subject, message.Subject ?? "");
            Header(labels.Date, message.Headers[HeaderId.Date] ?? ""); Header(labels.Attachments, attachmentCount.ToString(CultureInfo.InvariantCulture));
            result.Append("</table><p class='mail-notice'>").Append(Encode(labels.Notice)).Append("</p><article>").Append(body).Append("</article></body></html>");
            string prepared = result.ToString(); if (Encoding.UTF8.GetByteCount(prepared) > HtmlPrintDocument.MaxBytes) throw new EmailPrintException("Prepared mail exceeds 5 MiB.");
            return new(prepared, attachmentCount, attachments.AsReadOnly());
            void Header(string label, string value)
            { if (value.Length > 32768) throw new EmailPrintException("Decoded mail header limit exceeded."); result.Append("<tr><th>").Append(Encode(label)).Append("</th><td>").Append(Encode(value)).Append("</td></tr>"); }
            void Visit(MimeEntity entity, int depth)
            {
                ct.ThrowIfCancellationRequested(); if (++parts > 1000 || depth >= 32) throw new EmailPrintException("MIME structure limit exceeded.");
                if (entity is Multipart multi)
                {
                    if (multi.ContentType.MimeType.Equals("multipart/encrypted", StringComparison.OrdinalIgnoreCase)) throw new EmailPrintException("Encrypted mail is unsupported.");
                    foreach (var child in multi) Visit(child, depth + 1);
                }
                else leaves.Add(entity);
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException or InvalidOperationException)
        { throw new EmailPrintException("The EML message is invalid or unsupported.", ex); }
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
