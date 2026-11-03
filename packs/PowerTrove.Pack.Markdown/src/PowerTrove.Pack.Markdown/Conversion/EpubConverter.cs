using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PowerTrove.Pack.Markdown.Conversion;

/// <summary>
/// E-books (.epub) to Markdown: the chapters in reading order through the pack's HTML converter, their pictures
/// taken from the book, and links between chapters reduced to their text. Books with copy protection are refused.
/// </summary>
internal static partial class EpubConverter
{
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";

    public static async Task<ConvertedMarkdown> ConvertAsync(Stream source, IMarkdownEngine engine, CancellationToken ct)
    {
        using var zip = await ZipDocument.OpenAsync(source, ct).ConfigureAwait(false);
        // Font obfuscation is not protection; anything else encrypted is.
        if (zip.ReadXml("META-INF/encryption.xml")?.Root is { } encryption && encryption.Descendants().Any(e => e.Name.LocalName == "EncryptionMethod"
                && !((string?)e.Attribute("Algorithm") ?? string.Empty).Contains("obfuscation", StringComparison.OrdinalIgnoreCase)
                && !((string?)e.Attribute("Algorithm") ?? string.Empty).EndsWith("/fonts", StringComparison.OrdinalIgnoreCase)))
            throw new DocumentFormatException(SR.Get("ToMarkdownProtected"));
        string? package = (string?)zip.ReadXml("META-INF/container.xml")?.Root?.Descendants(Container + "rootfile").FirstOrDefault()?.Attribute("full-path");
        var opf = package is null ? null : zip.ReadXml(package)?.Root;
        if (package is null || opf is null) throw ZipDocument.Invalid();
        string folder = ZipDocument.Folder(package);
        var manifest = opf.Element(Opf + "manifest")?.Elements(Opf + "item")
            .Where(i => (string?)i.Attribute("id") is not null && (string?)i.Attribute("href") is not null)
            .GroupBy(i => (string)i.Attribute("id")!).ToDictionary(g => g.Key, g => g.First()) ?? [];

        var output = new MarkdownText();
        foreach (var itemref in opf.Element(Opf + "spine")?.Elements(Opf + "itemref") ?? [])
        {
            ct.ThrowIfCancellationRequested();
            if ((string?)itemref.Attribute("linear") == "no" || (string?)itemref.Attribute("idref") is not { } id || !manifest.TryGetValue(id, out var item)) continue;
            if (((string?)item.Attribute("properties") ?? string.Empty).Contains("nav", StringComparison.Ordinal)) continue;
            string path = ZipDocument.Resolve(folder, (string)item.Attribute("href")!);
            byte[]? bytes = zip.ReadBytes(path, 16L * 1024 * 1024);
            if (bytes is null) { output.OmittedContent++; continue; }
            string html;
            try { html = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { output.OmittedContent++; continue; }
            var conversion = await Task.Run(() => engine.ConvertHtml(html, ct), ct).ConfigureAwait(false);
            output.OmittedContent += conversion.RemovedContent;
            output.OmittedImages += conversion.OmittedImages;
            string chapterFolder = ZipDocument.Folder(path);
            string markdown = Image().Replace(conversion.Markdown, match =>
            {
                string target = match.Groups[2].Value.Trim('<', '>');
                if (target.Contains(':', StringComparison.Ordinal)) return match.Value;
                byte[]? image = zip.ReadBytes(ZipDocument.Resolve(chapterFolder, target), 25L * 1024 * 1024);
                if (image is not null && ImageKinds.Extension(image) is { } extension) return output.Image(image, extension, Unescape(match.Groups[1].Value));
                output.OmittedImages++;
                return match.Groups[1].Value;
            });
            // Links to other chapters point into the book; in one Markdown file only their text remains.
            markdown = InternalLink().Replace(markdown, "$1");
            output.Block(markdown);
        }
        if (output.IsEmpty) throw new DocumentFormatException(SR.Get("ToMarkdownNoContent"));
        return output.Build();
    }

    private static string Unescape(string text) => EscapedCharacter().Replace(text, "$1");

    [GeneratedRegex(@"!\[((?:[^\]\\\n]|\\.)*)\]\((<[^>\n]+>|[^)\s]+)(?: ""[^""\n]*"")?\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Image();

    [GeneratedRegex(@"(?<!!)\[((?:[^\]\\\n]|\\.)*)\]\((?!https?:|mailto:|#)(?:<[^>\n]+>|[^)\s]+)(?: ""[^""\n]*"")?\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex InternalLink();

    [GeneratedRegex(@"\\(.)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EscapedCharacter();
}
