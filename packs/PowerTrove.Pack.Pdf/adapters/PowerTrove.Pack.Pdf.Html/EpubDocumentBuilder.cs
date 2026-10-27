using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FileKit.Html;

namespace PowerTrove.Pack.Pdf.Html;

/// <summary>Reflows passive EPUB XHTML in spine order and embeds manifest-only resources.</summary>
public sealed class EpubDocumentBuilder : IEpubDocumentBuilder
{
    private readonly SafeHtmlMarkupValidator _validator = new();
    public HtmlPrintDocument Build(EpubBook book, CancellationToken cancellationToken)
    {
        var combined = new StringBuilder("<html><head><meta charset='utf-8'><style>.powertrove-epub-chapter{break-after:page}.powertrove-epub-chapter:last-child{break-after:auto}img{max-width:100%;height:auto}</style></head><body>");
        int index = 0, stylesheetReads = 0;
        foreach (var chapter in book.Chapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = _validator.ValidateBookChapter(book.ReadText(chapter));
            using var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument(normalized);
            foreach (var link in document.QuerySelectorAll("link"))
            {
                var item = book.ResolveItem(chapter.Path, link.GetAttribute("href") ?? "");
                string css = Stylesheet(item, new HashSet<string>(StringComparer.Ordinal));
                var style = document.CreateElement("style"); style.TextContent = css; link.Replace(style);
            }
            foreach (var style in document.QuerySelectorAll("style")) style.TextContent = Css(style.TextContent, chapter.Path, new HashSet<string>(StringComparer.Ordinal));
            foreach (var element in document.All)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (element.HasAttribute("style")) element.SetAttribute("style", Css(element.GetAttribute("style")!, chapter.Path, new HashSet<string>(StringComparer.Ordinal)));
                if (element.HasAttribute("srcset") || element.LocalName == "picture") throw new EpubException("Responsive EPUB pictures are unsupported.", EpubFailure.Unsupported);
                if (element.LocalName == "img") element.SetAttribute("src", Image(chapter.Path, element.GetAttribute("src") ?? ""));
                // A printed reflow does not retain navigable local/external file links.
                if (element.LocalName == "a") element.RemoveAttribute("href");
                if (element.HasAttribute("background")) element.SetAttribute("background", Image(chapter.Path, element.GetAttribute("background")!));
            }
            var section = document.CreateElement("section"); section.ClassName = "powertrove-epub-chapter"; section.SetAttribute("data-chapter", (++index).ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var attribute in document.DocumentElement.Attributes.Where(a => a.Name is "lang" or "dir")) section.SetAttribute(attribute.Name, attribute.Value);
            var body = document.Body ?? throw new EpubException("Missing EPUB chapter body.");
            var content = document.CreateElement("div");
            foreach (var attribute in body.Attributes.Where(a => a.Name is "class" or "style" or "lang" or "dir" or "id")) content.SetAttribute(attribute.Name, attribute.Value);
            content.InnerHtml = body.InnerHtml;
            // Head styles apply to the combined reflow, as in ordinary printed books.
            foreach (var style in document.Head!.QuerySelectorAll("style")) combined.Append(style.OuterHtml);
            section.AppendChild(content); combined.Append(section.OuterHtml);
            if (Encoding.UTF8.GetByteCount(combined.ToString()) > HtmlPrintDocument.MaxBytes) throw new EpubException("Prepared EPUB document exceeds 5 MiB.", EpubFailure.Limit);
        }
        combined.Append("</body></html>"); return HtmlPrintDocument.FromText(combined.ToString(), _validator);

        string Stylesheet(EpubItem item, HashSet<string> stack)
        {
            if (++stylesheetReads > 3000) throw new EpubException("EPUB stylesheet expansion limit exceeded.", EpubFailure.Limit);
            if (item.MediaType != "text/css") throw new EpubException("An EPUB stylesheet has an unsupported type.", EpubFailure.Unsupported);
            if (stack.Count >= 16 || !stack.Add(item.Path)) throw new EpubException("Cyclic or excessive EPUB CSS imports.", EpubFailure.Limit);
            try { return Css(book.ReadText(item), item.Path, stack); } finally { stack.Remove(item.Path); }
        }
        string Css(string css, string owner, HashSet<string> stack) => EpubCss.Rewrite(css, path => Asset(owner, path, false), path => Stylesheet(book.ResolveItem(owner, path), stack), cancellationToken);
        string Image(string owner, string path) => Asset(owner, path, true);
        string Asset(string owner, string path, bool imageOnly)
        {
            if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = path.IndexOf(','); if (comma < 0) throw new EpubException("Invalid EPUB data resource.");
                string type = path[5..comma]; if (!type.EndsWith(";base64", StringComparison.OrdinalIgnoreCase) || !Allowed(type[..^7], imageOnly)) throw new EpubException("Unsupported EPUB embedded resource.", EpubFailure.Unsupported);
                try { return "data:" + type[..^7] + ";base64," + Convert.ToBase64String(Convert.FromBase64String(path[(comma + 1)..])); }
                catch (FormatException ex) { throw new EpubException("Invalid EPUB data resource encoding.", inner: ex); }
            }
            var item = book.ResolveItem(owner, path);
            if (!Allowed(item.MediaType, imageOnly)) throw new EpubException("Unsupported EPUB image or font resource.", EpubFailure.Unsupported);
            return "data:" + item.MediaType + ";base64," + Convert.ToBase64String(book.Read(item).Span);
        }
    }
    private static bool Allowed(string type, bool imageOnly) => type is "image/png" or "image/jpeg" or "image/gif" or "image/webp" || !imageOnly && type is "font/woff" or "font/woff2" or "font/ttf" or "font/otf" or "application/font-woff" or "application/vnd.ms-opentype" or "application/font-sfnt";
}
