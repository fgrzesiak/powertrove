using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace PowerTrove.Pack.Markdown.Conversion;

/// <summary>
/// PowerPoint (.pptx) to Markdown: one section per slide in presentation order, the slide title as a heading,
/// text boxes in reading order (top to bottom, then left to right), content placeholders and bulleted text as
/// nested lists, tables, pictures and, optionally, the speaker notes. Hidden slides are left out.
/// </summary>
internal static class PptxConverter
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static async Task<ConvertedMarkdown> ConvertAsync(Stream source, string fileTitle, bool notes, CancellationToken ct)
    {
        using var zip = await ZipDocument.OpenAsync(source, ct).ConfigureAwait(false);
        string main = zip.Relationships("").Values.FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).Target ?? "ppt/presentation.xml";
        var presentation = zip.ReadXml(main)?.Root ?? throw ZipDocument.Invalid();
        var rels = zip.Relationships(main);
        var output = new MarkdownText();
        output.Block("# " + MarkdownText.Inline(fileTitle));
        int number = 0;
        foreach (var slideId in presentation.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        {
            ct.ThrowIfCancellationRequested();
            if ((string?)slideId.Attribute(R + "id") is not { } rid || !rels.TryGetValue(rid, out var rel) || rel.External) continue;
            var slide = zip.ReadXml(rel.Target)?.Root;
            if (slide is null || (string?)slide.Attribute("show") is "0" or "false") continue;
            number++;
            var slideRels = zip.Relationships(rel.Target);
            var shapes = Shapes(slide.Element(P + "cSld")?.Element(P + "spTree")).ToList();
            var title = shapes.FirstOrDefault(s => s.Placeholder is "title" or "ctrTitle");
            string heading = title is null ? string.Empty : MarkdownText.Clean(string.Join(" ", Paragraphs(title.Element).Select(p => PlainText(p))));
            output.Block("## " + (heading.Length > 0 ? MarkdownText.Inline(heading) : string.Format(CultureInfo.CurrentCulture, SR.Get("ToMarkdownSlide"), number)));
            foreach (var shape in shapes.Where(s => s != title).OrderBy(s => s.Y).ThenBy(s => s.X))
                Shape(shape, zip, slideRels, output);
            if (notes && slideRels.Values.FirstOrDefault(r => r.Type.EndsWith("/notesSlide", StringComparison.Ordinal)) is { Target: { Length: > 0 } notesPath } && zip.ReadXml(notesPath)?.Root is { } notesSlide)
            {
                var text = Shapes(notesSlide.Element(P + "cSld")?.Element(P + "spTree")).Where(s => s.Placeholder == "body")
                    .SelectMany(s => Paragraphs(s.Element)).Select(p => Runs(p, null)).Where(t => t.Trim().Length > 0).ToArray();
                if (text.Length > 0) output.Block("> **" + SR.Get("ToMarkdownNotes") + "**\n>\n> " + string.Join("\n>\n> ", text));
            }
        }
        if (number == 0) throw new DocumentFormatException(SR.Get("ToMarkdownNoContent"));
        return output.Build();
    }

    private sealed record ShapeInfo(XElement Element, string? Placeholder, long X, long Y);

    /// <summary>Shapes with their position, groups flattened.</summary>
    private static IEnumerable<ShapeInfo> Shapes(XElement? tree, int depth = 0)
    {
        if (tree is null || depth > 16) yield break;
        foreach (var element in tree.Elements())
        {
            if (element.Name == P + "grpSp") { foreach (var inner in Shapes(element, depth + 1)) yield return inner; continue; }
            if (element.Name.LocalName is not ("sp" or "graphicFrame" or "pic")) continue;
            var ph = element.Descendants(P + "ph").FirstOrDefault();
            string? type = ph is null ? null : (string?)ph.Attribute("type") ?? "body";
            var offset = element.Descendants(A + "off").FirstOrDefault();
            long.TryParse((string?)offset?.Attribute("x"), out long x);
            long.TryParse((string?)offset?.Attribute("y"), out long y);
            yield return new(element, type, x, y);
        }
    }

    private static IEnumerable<XElement> Paragraphs(XElement shape) => shape.Element(P + "txBody")?.Elements(A + "p") ?? [];

    private static void Shape(ShapeInfo shape, ZipDocument zip, IReadOnlyDictionary<string, (string Target, bool External, string Type)> rels, MarkdownText output)
    {
        var element = shape.Element;
        if (shape.Placeholder is "sldNum" or "dt" or "ftr" or "hdr") return;
        if (element.Name == P + "pic")
        {
            Picture(element, zip, rels, output);
            return;
        }
        if (element.Name == P + "graphicFrame")
        {
            var table = element.Descendants(A + "tbl").FirstOrDefault();
            if (table is null) { output.OmittedContent++; return; }
            var rows = table.Elements(A + "tr").Select(tr => (IReadOnlyList<string>)tr.Elements(A + "tc")
                .Select(tc => string.Join("<br>", tc.Descendants(A + "p").Select(p => Runs(p, rels)).Where(t => t.Trim().Length > 0))).ToArray()).ToArray();
            if (rows.Length > 0) output.Block(MarkdownText.Table(rows));
            return;
        }
        bool list = shape.Placeholder is "body" or "obj";
        var lines = new List<string>();
        foreach (var paragraph in Paragraphs(element))
        {
            string text = Runs(paragraph, rels).Trim();
            if (text.Length == 0)
            {
                if (lines.Count > 0) { Flush(); }
                continue;
            }
            var ppr = paragraph.Element(A + "pPr");
            int level = (int?)ppr?.Attribute("lvl") ?? 0;
            bool numbered = ppr?.Element(A + "buAutoNum") is not null;
            bool bullet = ppr?.Element(A + "buNone") is null && (list || ppr?.Element(A + "buChar") is not null || numbered);
            lines.Add(bullet ? new string(' ', Math.Clamp(level, 0, 8) * (numbered ? 3 : 2)) + (numbered ? "1. " : "- ") + text : MarkdownText.LineStart(text));
            if (!bullet) Flush();
        }
        Flush();

        void Flush()
        {
            if (lines.Count == 0) return;
            output.Block(string.Join("\n", lines));
            lines.Clear();
        }
    }

    private static void Picture(XElement picture, ZipDocument zip, IReadOnlyDictionary<string, (string Target, bool External, string Type)> rels, MarkdownText output)
    {
        string? id = (string?)picture.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed");
        string? alt = (string?)picture.Descendants(P + "cNvPr").FirstOrDefault()?.Attribute("descr");
        if (id is null || !rels.TryGetValue(id, out var rel) || rel.External) { output.OmittedImages++; return; }
        byte[]? bytes = zip.ReadBytes(rel.Target, 25L * 1024 * 1024);
        string? extension = bytes is null ? null : ImageKinds.Extension(bytes);
        if (bytes is null || extension is null) { output.OmittedImages++; return; }
        output.Block(output.Image(bytes, extension, alt));
    }

    private static string PlainText(XElement paragraph) => string.Concat(paragraph.Descendants(A + "t").Select(t => t.Value));

    /// <summary>Runs with bold, italic, strikethrough, monospace and link formatting.</summary>
    private static string Runs(XElement paragraph, IReadOnlyDictionary<string, (string Target, bool External, string Type)>? rels)
    {
        var builder = new StringBuilder();
        foreach (var element in paragraph.Elements())
        {
            if (element.Name == A + "br") { builder.Append("<br>"); continue; }
            if (element.Name != A + "r" && element.Name != A + "fld") continue;
            string raw = string.Concat(element.Elements(A + "t").Select(t => t.Value));
            string text = MarkdownText.Clean(raw);
            if (text.Length == 0) continue;
            if (raw.Length > 0 && char.IsWhiteSpace(raw[0]) && builder.Length > 0) builder.Append(' ');
            var rpr = element.Element(A + "rPr");
            string font = (string?)rpr?.Element(A + "latin")?.Attribute("typeface") ?? string.Empty;
            string formatted;
            if (DocxConverter.IsMonospace(font)) formatted = MarkdownText.Code(text);
            else
            {
                formatted = MarkdownText.Inline(text);
                string marker = ((string?)rpr?.Attribute("b") is "1" or "true" ? "**" : "") + ((string?)rpr?.Attribute("i") is "1" or "true" ? "*" : "");
                if (marker.Length > 0) formatted = marker + formatted + marker;
                if ((string?)rpr?.Attribute("strike") is "sngStrike" or "dblStrike") formatted = "~~" + formatted + "~~";
            }
            if ((string?)rpr?.Element(A + "hlinkClick")?.Attribute(R + "id") is { } link && rels is not null && rels.TryGetValue(link, out var target) && target.External
                && DocxConverter.SafeLink(target.Target) is { } url)
                formatted = "[" + formatted + "](" + url + ")";
            builder.Append(formatted);
            if (raw.Length > 1 && char.IsWhiteSpace(raw[^1])) builder.Append(' ');
        }
        return builder.ToString().Replace("  ", " ", StringComparison.Ordinal);
    }
}
