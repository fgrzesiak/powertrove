using PowerTrove.Pack.Markdown.Conversion;

namespace PowerTrove.Pack.Markdown.Tests;

/// <summary>Tagged PDFs: the structure tree's tags decide headings, tables, lists, paragraphs and artifacts.</summary>
public sealed class PdfTaggedTests
{
    private static PdfTextLine Line(string text, double x, double y, int? tag, double size = 10, bool bold = false, bool artifact = false)
    {
        var words = new List<PdfTextWord>();
        foreach (string part in text.Split(' '))
        {
            words.Add(new PdfTextWord(part, x, y, x + size * 0.5 * part.Length, y + size, size, bold, Tag: tag, Artifact: artifact));
            x += size * 0.5 * (part.Length + 1);
        }
        return new PdfTextLine(words);
    }

    private static string Convert(IReadOnlyList<PdfTextTag> tags, params PdfTextBlock[] blocks) =>
        PdfLayoutConverter.Convert(new PdfTextDocument([new PdfTextPage(1, 612, 792, blocks, [])], Tags: tags), default).Markdown;

    [Fact]
    public void TaggedHeadingsSetLevelsAndBodyTextTaggedAsHeadingStaysText()
    {
        PdfTextTag[] tags =
        [
            new(PdfTextTagKind.Heading, 1, 1), new(PdfTextTagKind.Paragraph, Group: 2),
            new(PdfTextTagKind.Heading, 3, 3), new(PdfTextTagKind.Heading, 2, 4)
        ];
        string markdown = Convert(tags,
            new([Line("Overview", 72, 700, 0, bold: true)]),
            new([Line("Some text.", 72, 680, 1)]),
            new([Line("Details", 72, 650, 2, bold: true)]),
            new([Line("This plain sentence was tagged as a heading by mistake.", 72, 620, 3)]));

        Assert.Equal("# Overview\n\nSome text.\n\n### Details\n\nThis plain sentence was tagged as a heading by mistake.\n", markdown);
    }

    [Fact]
    public void TaggedTablesNeedNoBordersAndPlaceWordsInTheirOwnCells()
    {
        PdfTextTag Cell(int row, int column, bool header = false) => new(PdfTextTagKind.TableCell, Group: 7, Row: row, Column: column, Header: header);
        PdfTextTag[] tags = [Cell(0, 0, true), Cell(0, 1, true), Cell(1, 0), Cell(1, 1), new(PdfTextTagKind.Paragraph, Group: 8)];
        // The header row is one line whose words belong to two cells.
        var header = new PdfTextLine([.. Line("Rule", 72, 700, 0).Words, .. Line("Meaning", 200, 700, 1).Words]);
        string markdown = Convert(tags,
            new([header]),
            new([new PdfTextLine([.. Line("Seed", 72, 680, 2).Words, .. Line("Same layout", 200, 680, 3).Words])]),
            new([Line("After the table.", 72, 640, 4)]));

        Assert.Equal("| Rule | Meaning |\n| --- | --- |\n| Seed | Same layout |\n\nAfter the table.\n", markdown);
    }

    [Fact]
    public void TaggedListsNestByTheirDepthAndDropSymbolBullets()
    {
        PdfTextTag[] tags = [new(PdfTextTagKind.ListItem, 1, 1), new(PdfTextTagKind.ListItem, 2, 2), new(PdfTextTagKind.ListItem, 1, 3)];
        string markdown = Convert(tags,
            new([Line(" First item", 72, 700, 0), Line("continues here", 84, 688, 0)]),
            new([Line(" Nested", 90, 670, 1)]),
            new([Line("1. Second", 72, 650, 2)]));

        Assert.Equal("- First item continues here\n   - Nested\n1. Second\n", markdown);
    }

    [Fact]
    public void ParagraphsFollowTheirElementsAcrossBlocksAndArtifactsAreDropped()
    {
        PdfTextTag[] tags = [new(PdfTextTagKind.Paragraph, Group: 1), new(PdfTextTagKind.Paragraph, Group: 2)];
        string markdown = Convert(tags,
            new([Line("Running header", 72, 760, null, artifact: true)]),
            new([Line("A sentence that ends.", 72, 700, 0)]),
            new([Line("Still the same paragraph.", 72, 600, 0)]),
            new([Line("A new paragraph", 72, 585, 1)]));

        Assert.Equal("A sentence that ends. Still the same paragraph.\n\nA new paragraph\n", markdown);
    }

    [Fact]
    public void FiguresKeepTheirAlternativeText()
    {
        var page = new PdfTextPage(1, 612, 792, [new([Line("Text below.", 72, 400, 0)])], [],
            [new PdfPageImage(72, 450, 300, 700, [1, 2, 3], ".png", "Sales per year")]);
        var result = PdfLayoutConverter.Convert(new PdfTextDocument([page], Tags: [new(PdfTextTagKind.Paragraph, Group: 1)]), default);

        Assert.StartsWith("![Sales per year](", result.Markdown);
    }
}
