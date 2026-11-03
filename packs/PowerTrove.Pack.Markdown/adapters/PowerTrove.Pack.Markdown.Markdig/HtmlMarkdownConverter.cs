using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace PowerTrove.Pack.Markdown.Markdig;

/// <summary>
/// Converts static HTML to GitHub-flavored Markdown. Headings, paragraphs, emphasis, links, lists, task lists,
/// quotes, code, tables, rules and images with linkable sources are kept; scripts, styles, forms, frames and
/// media are dropped; embedded (data) images are omitted and counted. Layout and colors are not preserved.
/// </summary>
internal sealed partial class HtmlMarkdownConverter
{
    private const int MaximumElements = 200_000;
    private const int MaximumDepth = 256;
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "iframe", "frame", "frameset", "object", "embed", "applet", "head", "title", "meta", "link",
        "base", "svg", "math", "canvas", "audio", "video", "source", "track", "map", "area", "select", "option", "textarea", "button", "dialog", "datalist"
    };
    private static readonly HashSet<string> Active = new(StringComparer.OrdinalIgnoreCase) { "script", "iframe", "frame", "frameset", "object", "embed", "applet" };
    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "center", "dd", "details", "div", "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form",
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre", "section", "summary", "table", "ul", "body", "html"
    };
    private int _elements;
    private int _omittedImages;
    private int _removed;
    private CancellationToken _token;

    public HtmlConversion Convert(string html, CancellationToken cancellationToken)
    {
        _token = cancellationToken;
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
        var document = parser.ParseDocument(html);
        var root = (INode?)document.Body ?? document.DocumentElement;
        var blocks = new List<string>();
        if (root is not null) Blocks(root, blocks, 0);
        string markdown = string.Join("\n\n", blocks.Where(b => b.Trim().Length > 0)).Trim();
        markdown = BlankRuns().Replace(markdown, "\n\n");
        return new HtmlConversion(markdown.Length == 0 ? string.Empty : markdown + "\n", _omittedImages, _removed);
    }

    private void Count(int depth)
    {
        if (depth > MaximumDepth) throw new MarkdownLimitException("HTML nesting exceeds the supported depth.");
        if (++_elements > MaximumElements) throw new MarkdownLimitException("HTML exceeds the supported element count.");
        if ((_elements & 1023) == 0) _token.ThrowIfCancellationRequested();
    }

    /// <summary>Appends the blocks of <paramref name="container"/>; runs of inline content become paragraphs.</summary>
    private void Blocks(INode container, List<string> output, int depth)
    {
        var inline = new List<INode>();
        void Flush()
        {
            if (inline.Count == 0) return;
            string text = InlineOf(inline, depth).Trim();
            inline.Clear();
            if (text.Length > 0) output.Add(EscapeLineStart(text));
        }
        foreach (var node in container.ChildNodes)
        {
            if (node is IElement element && (BlockElements.Contains(element.LocalName) || Skipped.Contains(element.LocalName)))
            {
                Flush();
                Block(element, output, depth + 1);
            }
            else if (node is IText or IElement) inline.Add(node);
        }
        Flush();
    }

    private void Block(IElement element, List<string> output, int depth)
    {
        Count(depth);
        string name = element.LocalName.ToLowerInvariant();
        if (Skipped.Contains(name)) { if (Active.Contains(name)) _removed++; return; }
        switch (name)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                string heading = Collapse(Inline(element, depth)).Trim();
                if (heading.Length > 0) output.Add(new string('#', name[1] - '0') + " " + heading);
                break;
            case "p":
                string paragraph = Inline(element, depth).Trim();
                if (paragraph.Length > 0) output.Add(EscapeLineStart(paragraph));
                break;
            case "hr":
                output.Add("---");
                break;
            case "pre":
                output.Add(Fence(element));
                break;
            case "blockquote":
                var quoted = new List<string>();
                Blocks(element, quoted, depth);
                if (quoted.Count > 0)
                    output.Add(string.Join("\n", string.Join("\n\n", quoted).Split('\n').Select(line => line.Length == 0 ? ">" : "> " + line)));
                break;
            case "ul" or "ol":
                output.Add(List(element, name == "ol", depth));
                break;
            case "table":
                output.Add(Table(element, depth));
                break;
            case "dl":
                foreach (var item in element.Children)
                {
                    string text = Collapse(Inline(item, depth)).Trim();
                    if (text.Length == 0) continue;
                    output.Add(item.LocalName.Equals("dt", StringComparison.OrdinalIgnoreCase) ? "**" + text + "**" : ": " + text);
                }
                break;
            case "summary":
                string summary = Collapse(Inline(element, depth)).Trim();
                if (summary.Length > 0) output.Add("**" + summary + "**");
                break;
            case "figcaption":
                string caption = Collapse(Inline(element, depth)).Trim();
                if (caption.Length > 0) output.Add("*" + caption + "*");
                break;
            default:
                // Transparent containers: div, section, article, main, figure, details, form, center...
                Blocks(element, output, depth);
                break;
        }
    }

    private string List(IElement list, bool ordered, int depth)
    {
        var lines = new List<string>();
        int number = ordered && int.TryParse(list.GetAttribute("start"), out int start) && start is >= 0 and < 1_000_000_000 ? start : 1;
        foreach (var item in list.Children.Where(c => c.LocalName.Equals("li", StringComparison.OrdinalIgnoreCase)))
        {
            Count(depth + 1);
            string marker = ordered ? number++ + ". " : "- ";
            string task = string.Empty;
            if (item.Children.FirstOrDefault(c => c.LocalName.Equals("input", StringComparison.OrdinalIgnoreCase)) is { } box
                && string.Equals(box.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase))
                task = box.HasAttribute("checked") ? "[x] " : "[ ] ";
            var blocks = new List<string>();
            Blocks(item, blocks, depth + 1);
            // Tight items (no paragraphs) keep nested lists directly below their text, as Markdown writers do.
            bool loose = item.Children.Any(c => c.LocalName.Equals("p", StringComparison.OrdinalIgnoreCase));
            var content = string.Join(loose ? "\n\n" : "\n", blocks).Split('\n');
            string indent = new(' ', marker.Length);
            lines.Add(marker + task + (content.Length > 0 ? content[0] : string.Empty));
            lines.AddRange(content.Skip(1).Select(line => line.Length == 0 ? string.Empty : indent + line));
        }
        return string.Join("\n", lines);
    }

    private string Table(IElement table, int depth)
    {
        var rows = table.QuerySelectorAll("tr").Where(r => r.Closest("table") == table).ToArray();
        if (rows.Length == 0) return string.Empty;
        var cells = rows.Select(r => r.Children.Where(c => c.LocalName is "td" or "th").ToArray()).ToArray();
        int columns = Math.Max(1, cells.Max(r => r.Length));
        string Cell(IElement cell)
        {
            Count(depth + 1);
            string text = Inline(cell, depth + 1, inTable: true).Trim();
            return Collapse(text).Replace("|", "\\|", StringComparison.Ordinal);
        }
        string Row(IElement[] row) => "| " + string.Join(" | ", Enumerable.Range(0, columns).Select(i => i < row.Length ? Cell(row[i]) : string.Empty)) + " |";
        var header = cells[0];
        string Align(int i)
        {
            string? align = i < header.Length ? (header[i].GetAttribute("align") ?? AlignStyle(header[i].GetAttribute("style"))) : null;
            return align?.ToLowerInvariant() switch { "center" => ":---:", "right" => "---:", "left" => ":---", _ => "---" };
        }
        var lines = new List<string> { Row(header), "| " + string.Join(" | ", Enumerable.Range(0, columns).Select(Align)) + " |" };
        lines.AddRange(cells.Skip(1).Select(Row));
        return string.Join("\n", lines);
    }

    private static string? AlignStyle(string? style) => style is null ? null : TextAlign().Match(style) is { Success: true } m ? m.Groups[1].Value : null;

    private static string Fence(IElement pre)
    {
        var code = pre.Children.FirstOrDefault(c => c.LocalName.Equals("code", StringComparison.OrdinalIgnoreCase));
        string text = (code ?? pre).TextContent.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        string? language = (code?.ClassList ?? pre.ClassList).Select(c => c.StartsWith("language-", StringComparison.Ordinal) ? c[9..] : c.StartsWith("lang-", StringComparison.Ordinal) ? c[5..] : null)
            .FirstOrDefault(l => l is not null && LanguageName().IsMatch(l));
        int longest = BacktickRuns().Matches(text).Select(m => m.Length).DefaultIfEmpty(0).Max();
        string fence = new('`', Math.Max(3, longest + 1));
        return fence + (language ?? string.Empty) + "\n" + text + "\n" + fence;
    }

    private string InlineOf(IEnumerable<INode> nodes, int depth)
    {
        var builder = new StringBuilder();
        foreach (var node in nodes) Inline(node, builder, depth, inTable: false);
        return builder.ToString();
    }

    private string Inline(IElement element, int depth, bool inTable = false)
    {
        var builder = new StringBuilder();
        foreach (var node in element.ChildNodes) Inline(node, builder, depth, inTable);
        return builder.ToString();
    }

    private void Inline(INode node, StringBuilder output, int depth, bool inTable)
    {
        if (node is IText text) { output.Append(EscapeText(Collapse(text.Data))); return; }
        if (node is not IElement element) return;
        Count(depth);
        string name = element.LocalName.ToLowerInvariant();
        if (Skipped.Contains(name)) { if (Active.Contains(name)) _removed++; return; }
        string inner() => Inline(element, depth + 1, inTable);
        switch (name)
        {
            case "br": output.Append(inTable ? "<br>" : "\\\n"); break;
            case "strong" or "b": Wrap(output, inner(), "**"); break;
            case "em" or "i" or "cite" or "dfn": Wrap(output, inner(), "*"); break;
            case "del" or "s" or "strike": Wrap(output, inner(), "~~"); break;
            case "code" or "kbd" or "samp" or "tt" or "var": output.Append(Code(element.TextContent)); break;
            case "sub" or "sup":
                string script = inner().Trim();
                if (script.Length > 0) output.Append('<').Append(name).Append('>').Append(script).Append("</").Append(name).Append('>');
                break;
            case "a": Link(element, inner(), output); break;
            case "img": Image(element, output); break;
            case "input":
                if (string.Equals(element.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase) && element.ParentElement?.LocalName != "li")
                    output.Append(element.HasAttribute("checked") ? "[x] " : "[ ] ");
                break;
            default:
                if (BlockElements.Contains(name))
                {
                    // Block content inside inline context (for example a div in a table cell) stays on one line.
                    var blocks = new List<string>();
                    Blocks(element, blocks, depth + 1);
                    output.Append(' ').Append(string.Join(inTable ? "<br>" : " ", blocks.Select(Collapse))).Append(' ');
                }
                else output.Append(inner());
                break;
        }
    }

    private static void Wrap(StringBuilder output, string content, string marker)
    {
        string trimmed = content.Trim();
        if (trimmed.Length == 0) { output.Append(content); return; }
        if (content.Length > 0 && char.IsWhiteSpace(content[0])) output.Append(' ');
        output.Append(marker).Append(trimmed).Append(marker);
        if (content.Length > 0 && char.IsWhiteSpace(content[^1])) output.Append(' ');
    }

    private void Link(IElement anchor, string content, StringBuilder output)
    {
        string href = (anchor.GetAttribute("href") ?? string.Empty).Trim();
        string text = content.Trim();
        if (href.Length == 0 || !IsLinkable(href, allowMail: true)) { output.Append(content); _removed += href.Length > 0 ? 1 : 0; return; }
        if (text.Length == 0) text = EscapeText(href);
        if (text == EscapeText(href) && Scheme().IsMatch(href)) { output.Append('<').Append(href).Append('>'); return; }
        output.Append('[').Append(text).Append("](").Append(Destination(href)).Append(Title(anchor)).Append(')');
    }

    private void Image(IElement image, StringBuilder output)
    {
        string source = (image.GetAttribute("src") ?? string.Empty).Trim();
        string alt = EscapeText(Collapse(image.GetAttribute("alt") ?? string.Empty).Trim());
        if (source.Length == 0 || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || !IsLinkable(source, allowMail: false))
        {
            _omittedImages++;
            output.Append(alt);
            return;
        }
        output.Append("![").Append(alt).Append("](").Append(Destination(source)).Append(Title(image)).Append(')');
    }

    private static string Title(IElement element)
    {
        string? title = element.GetAttribute("title");
        return string.IsNullOrWhiteSpace(title) ? string.Empty : " \"" + Collapse(title).Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static bool IsLinkable(string url, bool allowMail)
    {
        if (url.Any(char.IsControl) || url.Length > 8192) return false;
        if (url.StartsWith('#')) return allowMail;
        var scheme = Scheme().Match(url);
        if (!scheme.Success) return true; // Relative and protocol-relative targets stay links in the Markdown.
        return scheme.Groups[1].Value.ToLowerInvariant() switch { "http" or "https" => true, "mailto" => allowMail, _ => false };
    }

    private static string Destination(string url) =>
        url.Any(c => c is ' ' or '(' or ')' or '<' or '>') ? "<" + url.Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal) + ">" : url;

    private static string Code(string content)
    {
        string text = Collapse(content);
        if (text.Length == 0) return string.Empty;
        int longest = BacktickRuns().Matches(text).Select(m => m.Length).DefaultIfEmpty(0).Max();
        string fence = new('`', longest + 1);
        string pad = text.StartsWith('`') || text.EndsWith('`') ? " " : string.Empty;
        return fence + pad + text + pad + fence;
    }

    private static string Collapse(string text) => Whitespace().Replace(text, " ");

    private static string EscapeText(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool intraword = i > 0 && i < text.Length - 1 && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1]);
            if (c is '\\' or '`' or '*' or '[' or ']' or '<' or '~' || c == '_' && !intraword) builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>Text that would start a heading, quote, list or rule is escaped at the start of a paragraph.</summary>
    private static string EscapeLineStart(string text)
    {
        if (text.Length == 0) return text;
        if (OrderedMarker().Match(text) is { Success: true } marker) return text[..(marker.Length - 1)] + "\\" + text[(marker.Length - 1)..];
        return text[0] is '#' or '>' or '+' or '-' or '=' ? "\\" + text : text;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlankRuns();
    [GeneratedRegex(@"`+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BacktickRuns();
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.-]*):", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Scheme();
    [GeneratedRegex(@"^[0-9]{1,9}[.)]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OrderedMarker();
    [GeneratedRegex(@"^[A-Za-z0-9_+#.-]{1,32}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LanguageName();
    [GeneratedRegex(@"text-align\s*:\s*(left|right|center)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TextAlign();
}
