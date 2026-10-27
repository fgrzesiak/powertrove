using AngleSharp.Html.Parser;
using FileKit.Html;

namespace PowerTrove.Pack.Pdf.Html;

public sealed class SafeHtmlMarkupValidator : IHtmlMarkupValidator
{
    private static readonly string[] Unsupported = ["script", "iframe", "frame", "frameset", "object", "embed", "base", "template", "svg", "math", "link", "form", "input", "textarea", "select", "button", "audio", "video", "source", "track"];
    public string ValidateAndNormalize(string html)
        => Validate(html, false);
    internal string ValidateBookChapter(string html) => Validate(html, true);
    private static string Validate(string html, bool book)
    {
        // A standalone parser has no resource loader or script engine. Serialize its
        // parsed DOM so validation and browser input share the same interpretation.
        int tags = 0;
        var parserOptions = new HtmlParserOptions { IsScripting = false, OnToken = (token, _) =>
        {
            if (token.Type != HtmlTokenType.StartTag) return;
            if (++tags > 50000 || Unsupported.Where(name => !book || name != "link").Any(token.IsStartTag))
                throw new HtmlPrintException("Active HTML or foreign content is unsupported.");
        } };
        using var document = new HtmlParser(parserOptions).ParseDocument(html);
        int count = 0;
        foreach (var element in document.All)
        {
            if (++count > 50000 || element.NamespaceUri != "http://www.w3.org/1999/xhtml" ||
                element.LocalName is "script" or "iframe" or "frame" or "frameset" or "object" or "embed" or "base" or "template" ||
                element.LocalName == "meta" && element.HasAttribute("http-equiv") &&
                    !(book && string.Equals(element.GetAttribute("http-equiv"), "content-type", StringComparison.OrdinalIgnoreCase) &&
                      (element.GetAttribute("content")?.ToLowerInvariant() is "text/html; charset=utf-8" or "application/xhtml+xml; charset=utf-8")) ||
                element.Attributes.Any(a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || a.Name.Equals("srcdoc", StringComparison.OrdinalIgnoreCase)))
                throw new HtmlPrintException("Active HTML, automatic navigation or foreign content is unsupported.");
            if (element.LocalName == "link" && (!book || element.GetAttribute("rel") != "stylesheet" || !element.HasAttribute("href")))
                throw new HtmlPrintException("Only manifest stylesheets are supported in EPUB chapters.");
            int depth = 0;
            for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
                if (++depth > 128) throw new HtmlPrintException("HTML nesting limit exceeded.");
        }
        return document.DocumentElement.OuterHtml;
    }
}
