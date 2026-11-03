using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace PowerTrove.Pack.Markdown.Markdig;

internal sealed record SanitizedHtml(IElement Body, IReadOnlyList<string> ImageSources, string? Title, int Removed);

/// <summary>
/// Allowlist sanitizer for rendered Markdown. Elements outside the list are unwrapped (their text stays);
/// active, embedded and metadata elements are dropped with their content. Only harmless attributes remain;
/// links keep http, https, mailto, fragment and relative targets; styles survive only as table alignment.
/// The parser runs without scripting or resource loading. Image sources are held back for the caller to map.
/// </summary>
internal static partial class MarkdownHtmlSanitizer
{
    public const string SourceAttribute = "data-powertrove-src";
    private const int MaximumElements = 200_000;
    private const int MaximumDepth = 256;

    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "noscript", "noembed", "noframes", "template",
        "svg", "math", "link", "meta", "base", "title", "head", "audio", "video", "source", "track", "canvas", "map", "area",
        "textarea", "select", "option", "optgroup", "button", "dialog", "param", "picture", "portal", "slot", "xmp", "plaintext", "datalist", "output", "progress", "meter"
    };

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "address", "article", "aside", "b", "bdi", "bdo", "blockquote", "br", "caption", "cite", "code", "col", "colgroup",
        "dd", "del", "details", "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "hr", "i", "img", "ins", "kbd", "li", "main", "mark", "nav", "ol", "p", "pre", "q", "rp", "rt", "ruby", "s", "samp",
        "section", "small", "span", "strong", "sub", "summary", "sup", "table", "tbody", "td", "tfoot", "th", "thead", "time", "tr",
        "u", "ul", "var", "wbr"
    };

    private static readonly HashSet<string> GlobalAttributes = new(StringComparer.OrdinalIgnoreCase) { "id", "class", "title", "lang", "dir" };

    private static readonly Dictionary<string, string[]> ElementAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = ["href"], ["img"] = ["src", "alt", "width", "height"], ["td"] = ["colspan", "rowspan", "align"], ["th"] = ["colspan", "rowspan", "align", "scope"],
        ["ol"] = ["start", "reversed", "type"], ["li"] = ["value"], ["col"] = ["span"], ["colgroup"] = ["span"], ["details"] = ["open"], ["time"] = ["datetime"]
    };

    public static SanitizedHtml Sanitize(string html, CancellationToken cancellationToken)
    {
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
        // Not disposed: the caller serializes and clones the element tree later.
        var document = parser.ParseDocument("<!DOCTYPE html><html><head></head><body></body></html>");
        var body = document.Body!;
        var nodes = parser.ParseFragment(html, body);
        foreach (var node in nodes.ToArray()) body.AppendChild(node);
        var state = new State(cancellationToken);
        Clean(body, 0, state);
        string? title = body.QuerySelector("h1")?.TextContent;
        return new SanitizedHtml(body, state.Images, string.IsNullOrWhiteSpace(title) ? null : title.Trim(), state.Removed);
    }

    /// <summary>Final check on a mapped image source before it is written into a document.</summary>
    public static bool IsEmittableImageSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Any(char.IsControl)) return false;
        if (source.StartsWith(PowerTrove.Abstractions.DocumentPreviewData.ResourceBaseUri, StringComparison.Ordinal)) return true;
        if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return true;
        var scheme = Scheme().Match(source);
        if (!scheme.Success) return !source.StartsWith("//", StringComparison.Ordinal);
        return scheme.Groups[1].Value.ToLowerInvariant() is "http" or "https";
    }

    private sealed class State(CancellationToken token)
    {
        public readonly List<string> Images = [];
        public readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        public int Elements;
        public int Removed;
        public CancellationToken Token = token;
    }

    private static void Clean(INode parent, int depth, State state)
    {
        if (depth > MaximumDepth) throw new MarkdownLimitException("HTML nesting exceeds the supported depth.");
        foreach (var child in parent.ChildNodes.ToArray())
        {
            switch (child)
            {
                case IText: continue;
                case IElement element:
                    if (++state.Elements > MaximumElements) throw new MarkdownLimitException("HTML exceeds the supported element count.");
                    if ((state.Elements & 1023) == 0) state.Token.ThrowIfCancellationRequested();
                    CleanElement(element, depth, state);
                    break;
                default:
                    parent.RemoveChild(child); // Comments, processing instructions and doctypes.
                    break;
            }
        }
    }

    private static void CleanElement(IElement element, int depth, State state)
    {
        string name = element.LocalName;
        if (element.NamespaceUri != "http://www.w3.org/1999/xhtml" || Dropped.Contains(name)) { element.Remove(); state.Removed++; return; }
        if (name.Equals("input", StringComparison.OrdinalIgnoreCase))
        {
            // Markdig task lists render disabled checkboxes; show them as static symbols instead of form controls.
            if (string.Equals(element.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase))
            {
                var mark = element.Owner!.CreateElement("span");
                mark.ClassName = "task";
                mark.TextContent = element.HasAttribute("checked") ? "☑" : "☐";
                element.Replace(mark);
            }
            else { element.Remove(); state.Removed++; }
            return;
        }
        Clean(element, depth + 1, state);
        if (!Allowed.Contains(name))
        {
            // Unknown or presentational wrappers (font, center, form, label...): keep their already cleaned content.
            foreach (var child in element.ChildNodes.ToArray()) element.Parent!.InsertBefore(child, element);
            element.Remove(); state.Removed++;
            return;
        }
        CleanAttributes(element, name, state);
    }

    private static void CleanAttributes(IElement element, string name, State state)
    {
        string? alignment = null;
        foreach (var attribute in element.Attributes.ToArray())
        {
            string key = attribute.Name;
            if (key.StartsWith("on", StringComparison.OrdinalIgnoreCase)) { element.RemoveAttribute(key); state.Removed++; continue; }
            if (key.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                // Only table alignment survives, as a class; anything else in a style is reported as removed.
                alignment ??= Alignment(attribute.Value);
                if (TextAlign().Replace(attribute.Value, ";").Any(c => c != ';' && !char.IsWhiteSpace(c))) state.Removed++;
                element.RemoveAttribute(key); continue;
            }
            bool allowed = GlobalAttributes.Contains(key) || ElementAttributes.TryGetValue(name, out var specific) && specific.Contains(key, StringComparer.OrdinalIgnoreCase);
            if (!allowed) { element.RemoveAttribute(key); continue; }
            string value = attribute.Value;
            switch (key.ToLowerInvariant())
            {
                case "href":
                    if (!IsSafeLink(value)) { element.RemoveAttribute(key); state.Removed++; }
                    break;
                case "class":
                    string classes = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(IsKeptClass).Take(8));
                    if (classes.Length == 0) element.RemoveAttribute(key); else element.SetAttribute(key, classes);
                    break;
                case "id":
                    if (!Identifier().IsMatch(value)) element.RemoveAttribute(key);
                    break;
                case "dir":
                    if (value.ToLowerInvariant() is not ("ltr" or "rtl" or "auto")) element.RemoveAttribute(key);
                    break;
                case "lang":
                    if (!Language().IsMatch(value)) element.RemoveAttribute(key);
                    break;
                case "title":
                    if (value.Length > 1024) element.SetAttribute(key, value[..1024]);
                    break;
                case "align":
                    alignment ??= value.ToLowerInvariant() is "left" or "right" or "center" ? value.ToLowerInvariant() : null;
                    element.RemoveAttribute(key);
                    break;
                case "width" or "height" or "colspan" or "rowspan" or "start" or "value" or "span":
                    if (!Number().IsMatch(value)) element.RemoveAttribute(key);
                    break;
                case "type":
                    if (value is not ("1" or "a" or "A" or "i" or "I")) element.RemoveAttribute(key);
                    break;
                case "scope":
                    if (value.ToLowerInvariant() is not ("row" or "col" or "rowgroup" or "colgroup")) element.RemoveAttribute(key);
                    break;
                case "reversed" or "open":
                    element.SetAttribute(key, string.Empty);
                    break;
                case "datetime":
                    if (value.Length > 64) element.RemoveAttribute(key);
                    break;
                case "alt":
                    if (value.Length > 1024) element.SetAttribute(key, value[..1024]);
                    break;
            }
        }
        if (alignment is not null && name.ToLowerInvariant() is "td" or "th" or "p" or "div")
            element.ClassList.Add("align-" + alignment);
        if (name.Equals("img", StringComparison.OrdinalIgnoreCase))
        {
            string source = (element.GetAttribute("src") ?? string.Empty).Trim();
            element.RemoveAttribute("src");
            if (source.Length > 0 && source.Length <= 14_000_000)
            {
                element.SetAttribute(SourceAttribute, source);
                if (state.Seen.Add(source)) state.Images.Add(source);
            }
        }
    }

    private static bool IsKeptClass(string token) =>
        token.Length <= 64 && ClassToken().IsMatch(token) &&
        (token.StartsWith("language-", StringComparison.Ordinal) || token is "task-list-item" or "contains-task-list" or "task-list-item-checkbox"
            or "footnotes" or "footnote-ref" or "footnote-back-ref");

    private static string? Alignment(string style)
    {
        var match = TextAlign().Match(style);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static bool IsSafeLink(string value)
    {
        string link = new(value.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());
        if (link.Length == 0 || link.Length > 8192) return false;
        if (link.StartsWith('#')) return true;
        if (link.StartsWith("//", StringComparison.Ordinal)) return false;
        var scheme = Scheme().Match(link);
        if (!scheme.Success) return true;
        return scheme.Groups[1].Value.ToLowerInvariant() is "http" or "https" or "mailto";
    }

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.-]*):", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Scheme();
    [GeneratedRegex(@"^[A-Za-z0-9_\-:.]{1,128}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Identifier();
    [GeneratedRegex(@"^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8}){0,4}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Language();
    [GeneratedRegex(@"^[0-9]{1,5}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Number();
    [GeneratedRegex(@"^[A-Za-z0-9_+#.-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ClassToken();
    [GeneratedRegex(@"(?:^|;)\s*text-align\s*:\s*(left|right|center)\s*(?:;|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TextAlign();
}
