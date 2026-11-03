using FileKit.Pdf;

namespace PowerTrove.Pack.Markdown.Pdf;

/// <summary>
/// Turns a tagged PDF's structure tree into one tag per element that matters for Markdown, and maps each piece of
/// marked content (page, MCID) to the tag of its innermost such element. Headings (H1 to H6, or H nested in sections),
/// list items with their depth, table cells placed in a grid that honours row and column spans, figures with their
/// alternative text, code and paragraphs.
/// </summary>
internal sealed class PdfStructureTags
{
    public List<PdfTextTag> Tags { get; } = [];
    public Dictionary<(int Page, int Mcid), int> Content { get; } = [];
    private int _groups;

    public static PdfStructureTags Build(IReadOnlyList<PdfStructureElement> roots, CancellationToken ct)
    {
        var tags = new PdfStructureTags();
        foreach (var root in roots) tags.Walk(root, new Context(null, 0, 0), ct);
        return tags;
    }

    private sealed record Context(int? Tag, int ListDepth, int Sections);

    private void Walk(PdfStructureElement element, Context context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var inner = context;
        switch (element.Type)
        {
            case "H1" or "H2" or "H3" or "H4" or "H5" or "H6":
                inner = context with { Tag = Add(new PdfTextTag(PdfTextTagKind.Heading, element.Type[1] - '0', ++_groups)) };
                break;
            case "H":
                inner = context with { Tag = Add(new PdfTextTag(PdfTextTagKind.Heading, Math.Clamp(context.Sections, 1, 6), ++_groups)) };
                break;
            case "Sect" or "Part" or "Art":
                inner = context with { Sections = context.Sections + 1 };
                break;
            case "L":
                inner = context with { ListDepth = context.ListDepth + 1 };
                break;
            case "LI":
                inner = context with { Tag = Add(new PdfTextTag(PdfTextTagKind.ListItem, Math.Max(1, context.ListDepth), ++_groups)) };
                break;
            case "Table":
                Table(element, context, ct);
                return;
            case "Figure":
                inner = context with { Tag = Add(new PdfTextTag(PdfTextTagKind.Figure, Group: ++_groups, Alt: element.Alt)) };
                break;
            case "Code":
                inner = context with { Tag = Add(new PdfTextTag(PdfTextTagKind.Code, Group: ++_groups)) };
                break;
            case "P" or "Caption" or "BlockQuote" or "Quote" or "Note" when context.Tag is null:
                inner = context with { Tag = Add(new PdfTextTag(PdfTextTagKind.Paragraph, Group: ++_groups)) };
                break;
        }
        Assign(element, inner);
        foreach (var child in element.Children) Walk(child, inner, ct);
    }

    /// <summary>Rows (TR, also inside THead, TBody, TFoot) and cells (TH, TD) on a grid; a spanned slot is skipped.</summary>
    private void Table(PdfStructureElement table, Context context, CancellationToken ct)
    {
        int group = ++_groups;
        var rows = new List<PdfStructureElement>();
        void Rows(PdfStructureElement element, int depth)
        {
            foreach (var child in element.Children)
                if (child.Type == "TR") rows.Add(child);
                else if (depth < 4 && child.Type is "THead" or "TBody" or "TFoot") Rows(child, depth + 1);
        }
        Rows(table, 0);
        var occupied = new HashSet<(int, int)>();
        for (int row = 0; row < rows.Count && row < 10_000; row++)
        {
            int column = 0;
            foreach (var cell in rows[row].Children.Where(c => c.Type is "TH" or "TD"))
            {
                ct.ThrowIfCancellationRequested();
                while (occupied.Contains((row, column))) column++;
                int tag = Add(new PdfTextTag(PdfTextTagKind.TableCell, Group: group, Row: row, Column: column, ColumnSpan: cell.ColumnSpan, Header: cell.Type == "TH"));
                for (int r = 0; r < cell.RowSpan; r++)
                    for (int c = 0; c < cell.ColumnSpan; c++) occupied.Add((row + r, column + c));
                var inner = context with { Tag = tag };
                Assign(cell, inner);
                foreach (var child in cell.Children) Walk(child, inner, ct);
                column += cell.ColumnSpan;
            }
        }
        // Content outside rows (a caption, for example) keeps the surrounding tag.
        foreach (var child in table.Children.Where(c => c.Type is not ("TR" or "THead" or "TBody" or "TFoot"))) Walk(child, context, ct);
    }

    private int Add(PdfTextTag tag)
    {
        Tags.Add(tag);
        return Tags.Count - 1;
    }

    private void Assign(PdfStructureElement element, Context context)
    {
        if (context.Tag is not int tag) return;
        foreach (var content in element.Content) Content[(content.PageIndex, content.Mcid)] = tag;
    }
}
