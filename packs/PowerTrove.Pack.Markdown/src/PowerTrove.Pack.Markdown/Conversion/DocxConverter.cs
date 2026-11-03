using System.Text;
using System.Xml.Linq;

namespace PowerTrove.Pack.Markdown.Conversion;

/// <summary>
/// Word (.docx) to Markdown from the document's own structure: heading and title styles (or outline levels),
/// numbered and bulleted lists with their nesting, quotes, code styles and monospace paragraphs, bold, italic,
/// strikethrough and code runs, links, footnotes and endnotes, tables and pictures. Tables of contents, deleted
/// revisions and field codes are left out. A document without heading styles gets headings from short paragraphs
/// that are bold throughout.
/// </summary>
internal static class DocxConverter
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    public static async Task<ConvertedMarkdown> ConvertAsync(Stream source, CancellationToken ct)
    {
        using var zip = await ZipDocument.OpenAsync(source, ct).ConfigureAwait(false);
        string main = zip.Relationships("").Values.FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).Target ?? "word/document.xml";
        var document = zip.ReadXml(main) ?? throw ZipDocument.Invalid();
        var body = document.Root?.Element(W + "body") ?? throw ZipDocument.Invalid();
        var context = new Context(zip, main, ct);
        context.Load(body);
        context.Walk(body.Elements());
        context.Finish();
        return context.Output.Build();
    }

    private enum Kind { Paragraph, Heading, ListItem, Quote, Code, Table, Raw }

    private sealed record Block(Kind Kind, string Text, int Level = 0, bool Ordered = false, bool AllBold = false, string Plain = "");

    private sealed record Style(string? Name, string? BasedOn, int? Outline, string? NumId, int Level);

    private sealed record Segment(string Text, bool Bold, bool Italic, bool Strike, bool Code, string? Link, bool Literal = false);

    private sealed class Context(ZipDocument zip, string main, CancellationToken ct)
    {
        public MarkdownText Output { get; } = new();
        private readonly Dictionary<string, Style> _styles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<int, bool>> _numbering = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XElement> _footnotes = new(StringComparer.Ordinal), _endnotes = new(StringComparer.Ordinal);
        private readonly List<(string Label, XElement Note)> _usedNotes = [];
        private readonly HashSet<string> _usedLabels = new(StringComparer.Ordinal);
        private IReadOnlyDictionary<string, (string Target, bool External, string Type)> _rels = new Dictionary<string, (string, bool, string)>();
        private readonly List<Block> _blocks = [];
        private bool _hasTitle;
        // Complex fields: the instruction of each open field and whether its result is being read.
        private readonly Stack<(StringBuilder Instruction, bool InResult)> _fields = new();

        public void Load(XElement body)
        {
            _rels = zip.Relationships(main);
            string folder = ZipDocument.Folder(main);
            string Part(string type, string fallback) => _rels.Values.FirstOrDefault(r => !r.External && r.Type.EndsWith(type, StringComparison.Ordinal)).Target ?? ZipDocument.Resolve(folder, fallback);

            foreach (var style in zip.ReadXml(Part("/styles", "styles.xml"))?.Root?.Elements(W + "style") ?? [])
            {
                string? id = (string?)style.Attribute(W + "styleId");
                if (id is null) continue;
                var ppr = style.Element(W + "pPr");
                _styles[id] = new((string?)style.Element(W + "name")?.Attribute(W + "val"), (string?)style.Element(W + "basedOn")?.Attribute(W + "val"),
                    (int?)ppr?.Element(W + "outlineLvl")?.Attribute(W + "val"), (string?)ppr?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val"),
                    (int?)ppr?.Element(W + "numPr")?.Element(W + "ilvl")?.Attribute(W + "val") ?? 0);
            }

            var numbering = zip.ReadXml(Part("/numbering", "numbering.xml"))?.Root;
            if (numbering is not null)
            {
                var abstracts = numbering.Elements(W + "abstractNum").ToDictionary(a => (string?)a.Attribute(W + "abstractNumId") ?? "", a => a.Elements(W + "lvl")
                    .GroupBy(l => (int?)l.Attribute(W + "ilvl") ?? 0).ToDictionary(g => g.Key, g => (string?)g.First().Element(W + "numFmt")?.Attribute(W + "val") is not ("bullet" or "none" or null)));
                foreach (var num in numbering.Elements(W + "num"))
                    if ((string?)num.Attribute(W + "numId") is { } numId && abstracts.TryGetValue((string?)num.Element(W + "abstractNumId")?.Attribute(W + "val") ?? "", out var levels))
                        _numbering[numId] = levels;
            }

            foreach (var (type, file, target) in new[] { ("/footnotes", "footnotes.xml", _footnotes), ("/endnotes", "endnotes.xml", _endnotes) })
                foreach (var note in zip.ReadXml(Part(type, file))?.Root?.Elements() ?? [])
                    if ((string?)note.Attribute(W + "id") is { } id && (string?)note.Attribute(W + "type") is null or "normal") target[id] = note;

            _hasTitle = body.Descendants(W + "p").Any(p => StyleName(p)?.Equals("Title", StringComparison.OrdinalIgnoreCase) == true);
        }

        public void Walk(IEnumerable<XElement> elements)
        {
            foreach (var element in elements)
            {
                ct.ThrowIfCancellationRequested();
                switch (element.Name.LocalName)
                {
                    case "p": Paragraph(element); break;
                    case "tbl": Table(element); break;
                    case "sdt":
                        // A table of contents is navigation Word rebuilds; the headings carry the structure.
                        if ((string?)element.Element(W + "sdtPr")?.Descendants(W + "docPartGallery").FirstOrDefault()?.Attribute(W + "val") is { } gallery
                            && gallery.Contains("Table of Contents", StringComparison.OrdinalIgnoreCase)) break;
                        Walk(element.Element(W + "sdtContent")?.Elements() ?? []);
                        break;
                    case "customXml": case "ins": case "smartTag": Walk(element.Elements()); break;
                }
            }
        }

        public void Finish()
        {
            // Without heading styles, short paragraphs in bold throughout read as headings.
            bool styled = _blocks.Any(b => b.Kind == Kind.Heading);
            Kind? previous = null;
            bool? previousOrdered = null;
            var code = new List<string>();
            void FlushCode()
            {
                if (code.Count > 0) Output.Block(MarkdownText.Fence(string.Join('\n', code)));
                code.Clear();
            }
            foreach (var block in _blocks)
            {
                if (block.Kind == Kind.Code) { code.Add(block.Text); previous = Kind.Code; continue; }
                FlushCode();
                switch (block.Kind)
                {
                    case Kind.Heading: Output.Block(new string('#', Math.Clamp(block.Level, 1, 6)) + " " + block.Text); break;
                    case Kind.ListItem:
                        string item = new string(' ', block.Level) + (block.Ordered ? "1. " : "- ") + block.Text;
                        // A top-level item of the other list type starts a new list.
                        if (previous == Kind.ListItem && !(block.Level == 0 && previousOrdered != block.Ordered)) Output.Line(item); else Output.Block(item);
                        if (block.Level == 0) previousOrdered = block.Ordered;
                        break;
                    case Kind.Quote: Output.Block("> " + block.Text.Replace("\n", "\n> ", StringComparison.Ordinal)); break;
                    case Kind.Paragraph when !styled && block.AllBold && block.Plain.Length is > 0 and <= 100 && !MarkdownText.EndsWithAny(block.Plain, ".,;:"):
                        Output.Block("## " + MarkdownText.Inline(block.Plain));
                        break;
                    default: Output.Block(block.Text); break;
                }
                previous = block.Kind;
            }
            FlushCode();
            foreach (var (label, note) in _usedNotes)
            {
                string text = string.Join(" ", note.Elements(W + "p").Select(p => Render(Segments(p), suppressBold: false).Trim()).Where(t => t.Length > 0));
                if (text.Length > 0) Output.Block("[^" + label + "]: " + text);
            }
        }

        private string? StyleName(XElement paragraph) =>
            (string?)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") is { } id && _styles.TryGetValue(id, out var style) ? style.Name ?? id : null;

        private IEnumerable<Style> StyleChain(string? id)
        {
            for (int depth = 0; id is not null && depth < 16 && _styles.TryGetValue(id, out var style); depth++, id = style.BasedOn) yield return style;
        }

        private int? HeadingLevel(XElement paragraph)
        {
            var ppr = paragraph.Element(W + "pPr");
            int? outline = (int?)ppr?.Element(W + "outlineLvl")?.Attribute(W + "val");
            string? id = (string?)ppr?.Element(W + "pStyle")?.Attribute(W + "val");
            int shift = _hasTitle ? 1 : 0;
            foreach (var style in StyleChain(id))
            {
                string name = style.Name ?? string.Empty;
                if (name.Equals("Title", StringComparison.OrdinalIgnoreCase)) return 1;
                if (name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[8..], out int level)) return level + shift;
                outline ??= style.Outline;
            }
            return outline is >= 0 and < 9 ? outline + 1 + shift : null;
        }

        private void Paragraph(XElement paragraph)
        {
            string? styleName = StyleName(paragraph);
            if (styleName is not null && (styleName.StartsWith("toc ", StringComparison.OrdinalIgnoreCase) || styleName.Equals("TOC Heading", StringComparison.OrdinalIgnoreCase))) return;
            var segments = Segments(paragraph);
            string plain = string.Concat(segments.Where(s => !s.Literal).Select(s => s.Text));
            if (segments.All(s => s.Literal ? s.Text.Length == 0 : string.IsNullOrWhiteSpace(s.Text))) return;

            var ppr = paragraph.Element(W + "pPr");
            string? styleId = (string?)ppr?.Element(W + "pStyle")?.Attribute(W + "val");
            var chain = StyleChain(styleId).ToArray();
            string names = string.Join("|", chain.Select(s => s.Name ?? string.Empty));

            if (HeadingLevel(paragraph) is int level)
            {
                _blocks.Add(new(Kind.Heading, Render(segments, suppressBold: true).Trim(), level));
                return;
            }
            bool monospace = segments.Where(s => !s.Literal && s.Text.Trim().Length > 0).All(s => s.Code);
            if (names.Contains("code", StringComparison.OrdinalIgnoreCase) || names.Contains("preformatted", StringComparison.OrdinalIgnoreCase)
                || names.Contains("source", StringComparison.OrdinalIgnoreCase) || monospace && plain.Trim().Length > 0)
            {
                _blocks.Add(new(Kind.Code, plain.Replace("\t", "    ", StringComparison.Ordinal)));
                return;
            }

            var numPr = ppr?.Element(W + "numPr");
            string? numId = (string?)numPr?.Element(W + "numId")?.Attribute(W + "val") ?? chain.Select(s => s.NumId).FirstOrDefault(n => n is not null);
            int ilvl = (int?)numPr?.Element(W + "ilvl")?.Attribute(W + "val") ?? chain.Select(s => (int?)s.Level).FirstOrDefault() ?? 0;
            string text = Render(segments, suppressBold: false).Trim();
            if (numId is not null and not "0")
            {
                bool ordered = _numbering.TryGetValue(numId, out var levels) && levels.TryGetValue(ilvl, out bool o) && o;
                if (!_numbering.ContainsKey(numId)) ordered = names.Contains("number", StringComparison.OrdinalIgnoreCase);
                _blocks.Add(new(Kind.ListItem, text, Indent(Math.Clamp(ilvl, 0, 8), ordered), ordered));
                return;
            }
            if (names.Contains("List Bullet", StringComparison.OrdinalIgnoreCase) || names.Contains("List Number", StringComparison.OrdinalIgnoreCase))
            {
                bool ordered = names.Contains("List Number", StringComparison.OrdinalIgnoreCase);
                _blocks.Add(new(Kind.ListItem, text, 0, ordered));
                return;
            }
            if (names.Contains("Quote", StringComparison.OrdinalIgnoreCase)) { _blocks.Add(new(Kind.Quote, text)); return; }
            bool allBold = segments.Where(s => !s.Literal && s.Text.Trim().Length > 0).All(s => s.Bold) && segments.Any(s => !s.Literal && s.Text.Trim().Length > 0);
            _blocks.Add(new(Kind.Paragraph, MarkdownText.LineStart(text), AllBold: allBold, Plain: plain.Trim()));
        }

        // Nested items are indented to the content of their parent: two spaces under "- ", three under "1. ".
        private readonly int[] _widths = new int[9];
        private int Indent(int level, bool ordered)
        {
            _widths[level] = ordered ? 3 : 2;
            int indent = 0;
            for (int i = 0; i < level; i++) indent += _widths[i] == 0 ? 2 : _widths[i];
            return indent;
        }

        private void Table(XElement table)
        {
            var rows = table.Elements(W + "tr").Select(row => row.Elements(W + "tc").ToArray()).Where(r => r.Length > 0).ToArray();
            if (rows.Length == 0) return;
            // A one-cell table is layout, not data: its content reads as normal paragraphs.
            if (rows.Length == 1 && rows[0].Length == 1) { Walk(rows[0][0].Elements()); return; }
            var cells = new List<IReadOnlyList<string>>();
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var values = new List<string>();
                foreach (var cell in row)
                {
                    var tcPr = cell.Element(W + "tcPr");
                    bool continued = tcPr?.Element(W + "vMerge") is { } merge && (string?)merge.Attribute(W + "val") is null or "continue";
                    string text = continued ? string.Empty : string.Join("<br>", cell.Descendants(W + "p").Select(p => Render(Segments(p), suppressBold: false).Trim()).Where(t => t.Length > 0));
                    values.Add(text);
                    int span = (int?)tcPr?.Element(W + "gridSpan")?.Attribute(W + "val") ?? 1;
                    for (int i = 1; i < Math.Min(span, 64); i++) values.Add(string.Empty);
                }
                cells.Add(values);
            }
            _blocks.Add(new(Kind.Table, MarkdownText.Table(cells)));
        }

        private List<Segment> Segments(XElement paragraph)
        {
            var segments = new List<Segment>();
            Inline(paragraph.Elements().Where(e => e.Name != W + "pPr"), null, segments);
            return segments;
        }

        private void Inline(IEnumerable<XElement> elements, string? link, List<Segment> segments)
        {
            foreach (var element in elements)
            {
                switch (element.Name.LocalName)
                {
                    case "r": Run(element, link, segments); break;
                    case "hyperlink":
                        string? target = (string?)element.Attribute(R + "id") is { } id && _rels.TryGetValue(id, out var rel) && rel.External ? rel.Target : null;
                        Inline(element.Elements(), SafeLink(target) ?? link, segments);
                        break;
                    case "fldSimple":
                        Inline(element.Elements(), SafeLink(FieldLink((string?)element.Attribute(W + "instr"))) ?? link, segments);
                        break;
                    case "ins": case "smartTag": case "customXml": case "bdo": case "dir": case "moveTo":
                        Inline(element.Elements(), link, segments);
                        break;
                    case "sdt": Inline(element.Element(W + "sdtContent")?.Elements() ?? [], link, segments); break;
                    case "oMath": case "oMathPara":
                        string math = string.Concat(element.Descendants(M + "t").Select(t => t.Value));
                        if (math.Length > 0) segments.Add(new(math, false, false, false, true, null));
                        break;
                }
            }
        }

        private void Run(XElement run, string? link, List<Segment> segments)
        {
            var rpr = run.Element(W + "rPr");
            string? charStyle = (string?)rpr?.Element(W + "rStyle")?.Attribute(W + "val") is { } sid && _styles.TryGetValue(sid, out var cs) ? cs.Name : null;
            bool On(string name) => rpr?.Element(W + name) is { } flag && (string?)flag.Attribute(W + "val") is null or "1" or "true" or "on";
            bool bold = On("b") || charStyle?.Contains("Strong", StringComparison.OrdinalIgnoreCase) == true;
            bool italic = On("i") || charStyle?.Contains("Emphasis", StringComparison.OrdinalIgnoreCase) == true;
            bool strike = On("strike") || On("dstrike");
            string font = (string?)rpr?.Element(W + "rFonts")?.Attribute(W + "ascii") ?? string.Empty;
            bool code = charStyle?.Contains("Code", StringComparison.OrdinalIgnoreCase) == true || IsMonospace(font);
            string? fieldLink = _fields.Count > 0 && _fields.Peek().InResult ? SafeLink(FieldLink(_fields.Peek().Instruction.ToString())) : null;
            bool hidden = _fields.Count > 0 && !_fields.Peek().InResult;

            foreach (var child in run.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "fldChar":
                        string kind = (string?)child.Attribute(W + "fldCharType") ?? string.Empty;
                        if (kind == "begin") _fields.Push((new StringBuilder(), false));
                        else if (kind == "separate" && _fields.Count > 0) _fields.Push((_fields.Pop().Instruction, true));
                        else if (kind == "end" && _fields.Count > 0) _fields.Pop();
                        fieldLink = _fields.Count > 0 && _fields.Peek().InResult ? SafeLink(FieldLink(_fields.Peek().Instruction.ToString())) : null;
                        hidden = _fields.Count > 0 && !_fields.Peek().InResult;
                        break;
                    case "instrText": if (_fields.Count > 0 && _fields.Peek().Instruction.Length < 2048) _fields.Peek().Instruction.Append(child.Value); break;
                    case "t" when !hidden: segments.Add(new(child.Value, bold, italic, strike, code, fieldLink ?? link)); break;
                    case "tab" when !hidden: segments.Add(new(" ", bold, italic, strike, code, fieldLink ?? link)); break;
                    case "noBreakHyphen" when !hidden: segments.Add(new("-", bold, italic, strike, code, fieldLink ?? link)); break;
                    case "br" or "cr" when !hidden && (string?)child.Attribute(W + "type") is null or "textWrapping":
                        segments.Add(new("\\\n", false, false, false, false, null, Literal: true));
                        break;
                    case "footnoteReference": Note(child, _footnotes, "", segments); break;
                    case "endnoteReference": Note(child, _endnotes, "e", segments); break;
                    case "drawing": case "pict": case "object": Picture(child, segments); break;
                }
            }
        }

        private void Note(XElement reference, Dictionary<string, XElement> notes, string prefix, List<Segment> segments)
        {
            if ((string?)reference.Attribute(W + "id") is not { } id || !notes.TryGetValue(id, out var note)) return;
            string label = prefix + id;
            if (_usedLabels.Add(label)) _usedNotes.Add((label, note));
            segments.Add(new("[^" + label + "]", false, false, false, false, null, Literal: true));
        }

        private void Picture(XElement container, List<Segment> segments)
        {
            string? id = (string?)container.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed") ?? (string?)container.Descendants(V + "imagedata").FirstOrDefault()?.Attribute(R + "id");
            string? alt = (string?)container.Descendants(Wp + "docPr").FirstOrDefault()?.Attribute("descr") ?? (string?)container.Descendants(Wp + "docPr").FirstOrDefault()?.Attribute("title");
            if (id is null || !_rels.TryGetValue(id, out var rel) || rel.External) { Output.OmittedContent++; return; }
            byte[]? bytes = zip.ReadBytes(rel.Target, 25L * 1024 * 1024);
            string? extension = bytes is null ? null : ImageKinds.Extension(bytes);
            if (bytes is null || extension is null) { Output.OmittedImages++; return; }
            segments.Add(new(Output.Image(bytes, extension, alt), false, false, false, false, null, Literal: true));
        }

        /// <summary>Joins runs with the same formatting so markers are not repeated inside words.</summary>
        private static string Render(IReadOnlyList<Segment> segments, bool suppressBold)
        {
            var builder = new StringBuilder();
            int i = 0;
            while (i < segments.Count)
            {
                var first = segments[i];
                if (first.Literal) { builder.Append(first.Text); i++; continue; }
                var text = new StringBuilder();
                int j = i;
                while (j < segments.Count && !segments[j].Literal && Same(segments[j], first)) text.Append(segments[j++].Text);
                builder.Append(Format(text.ToString(), first, suppressBold));
                i = j;
            }
            return builder.ToString();

            static bool Same(Segment a, Segment b) => a.Bold == b.Bold && a.Italic == b.Italic && a.Strike == b.Strike && a.Code == b.Code && a.Link == b.Link;
        }

        private static string Format(string raw, Segment style, bool suppressBold)
        {
            string text = MarkdownText.Clean(raw.Replace('\t', ' '));
            if (raw.Length > 0 && char.IsWhiteSpace(raw[0])) text = " " + text;
            if (raw.Length > 1 && char.IsWhiteSpace(raw[^1]) && text.Length > 0 && !text.EndsWith(' ')) text += " ";
            if (text.Trim().Length == 0) return text;
            string result;
            if (style.Code) result = (text.StartsWith(' ') ? " " : "") + MarkdownText.Code(text) + (text.Length > 1 && text.EndsWith(' ') ? " " : "");
            else
            {
                result = MarkdownText.Inline(text);
                string marker = (style.Bold && !suppressBold ? "**" : "") + (style.Italic ? "*" : "");
                if (marker.Length > 0) result = MarkdownText.Wrap(result, marker);
                if (style.Strike) result = MarkdownText.Wrap(result, "~~");
            }
            if (style.Link is not null) result = "[" + result.Trim() + "](" + style.Link + ")";
            return result;
        }

        private static string? FieldLink(string? instruction)
        {
            if (instruction is null) return null;
            string text = instruction.Trim();
            if (!text.StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase)) return null;
            int open = text.IndexOf('"'), close = open < 0 ? -1 : text.IndexOf('"', open + 1);
            return close > open ? text[(open + 1)..close] : null;
        }
    }

    /// <summary>Web and mail addresses only; spaces and parentheses are encoded so the link stays one destination.</summary>
    internal static string? SafeLink(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 2048) return null;
        string value = target.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto")) return null;
        var builder = new StringBuilder();
        foreach (char c in value) builder.Append(c is ' ' ? "%20" : c is '(' ? "%28" : c is ')' ? "%29" : c is '<' ? "%3C" : c is '>' ? "%3E" : char.IsControl(c) ? string.Empty : c.ToString());
        return builder.ToString();
    }

    internal static bool IsMonospace(string font) =>
        font.Contains("Consolas", StringComparison.OrdinalIgnoreCase) || font.Contains("Courier", StringComparison.OrdinalIgnoreCase)
        || font.Contains("Mono", StringComparison.OrdinalIgnoreCase) || font.Contains("Menlo", StringComparison.OrdinalIgnoreCase) || font.Contains("Cascadia Code", StringComparison.OrdinalIgnoreCase);
}
