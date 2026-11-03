using FileKit.Tabular;
using PowerTrove.Pack.Markdown.Conversion;

namespace PowerTrove.Pack.Markdown.Tests;

/// <summary>Tables with and without drawn grids in PDFs, converted from hand-built positioned pages.</summary>
public sealed class PdfTableTests
{
    private static PdfTextLine Line(string text, double x, double y, double size = 10)
    {
        var words = new List<PdfTextWord>();
        foreach (string part in text.Split(' '))
        {
            words.Add(new(part, x, y, x + size * 0.5 * part.Length, y + size, size));
            x += size * 0.5 * (part.Length + 1);
        }
        return new PdfTextLine(words);
    }

    private static string Convert(IReadOnlyList<PdfTextBlock> blocks, IReadOnlyList<TableRule>? rules = null) =>
        PdfLayoutConverter.Convert(new PdfTextDocument([new PdfTextPage(1, 612, 792, blocks, rules ?? [], null)]), default).Markdown;

    /// <summary>A caption, then rows of cells at three column positions, each cell its own line as a PDF reader splits them.</summary>
    private static List<PdfTextBlock> Table(string caption, params string[][] rows)
    {
        var blocks = new List<PdfTextBlock> { new([Line("Some text before the table.", 72, 720)]), new([Line(caption, 72, 690)]) };
        double[] columns = [72, 200, 330];
        for (int r = 0; r < rows.Length; r++)
            blocks.Add(new([.. rows[r].Select((cell, c) => Line(cell, columns[c], 670 - r * 14)).Where(l => l.Words[0].Text.Length > 0)]));
        blocks.Add(new([Line("Some text after the table.", 72, 670 - rows.Length * 14 - 30)]));
        return blocks;
    }

    [Fact]
    public void ACaptionedTableWithoutRulesBecomesAMarkdownTable()
    {
        string markdown = Convert(Table("Table 2 Units installed",
            ["Category", "2015", "2016"], ["Consumer", "3023", "4024"], ["Business", "1102", "1207"], ["Total", "4125", "5231"]));

        Assert.Contains("Table 2 Units installed\n\n| Category | 2015 | 2016 |\n| --- | --- | --- |\n| Consumer | 3023 | 4024 |\n| Business | 1102 | 1207 |\n| Total | 4125 | 5231 |\n\nSome text after", markdown);
    }

    [Fact]
    public void AWrappedCellContinuesItsRow()
    {
        string markdown = Convert(Table("Table 3 Sensors",
            ["Sensor", "Range", "Use"], ["Inductive", "up to 60 mm", "Metal targets"], ["", "", "in machines"], ["Optical", "up to 3 m", "Objects"]));

        Assert.Contains("| Inductive | up to 60 mm | Metal targets in machines |", markdown);
        Assert.Contains("| Optical | up to 3 m | Objects |", markdown);
    }

    [Fact]
    public void ABulletListSetInTwoColumnsIsNoTable()
    {
        string markdown = Convert(Table("Table 4 Notes", ["•", "First point"], ["•", "Second point"], ["•", "Third point"], ["•", "Fourth point"]));

        Assert.DoesNotContain("| --- |", markdown);
    }

    [Fact]
    public void AlignedTextWithoutCaptionOrRulesNeedsThreeColumnsAndFourRows()
    {
        // Two columns of short lines and no sign of a table stay text.
        var blocks = new List<PdfTextBlock> { new([Line("Name", 72, 700), Line("Value", 200, 700)]), new([Line("Alpha", 72, 686), Line("One", 200, 686)]),
            new([Line("Beta", 72, 672), Line("Two", 200, 672)]), new([Line("Gamma", 72, 658), Line("Three", 200, 658)]) };

        Assert.DoesNotContain("| --- |", Convert(blocks));
        // A rule above them is such a sign.
        Assert.Contains("| Name | Value |", Convert(blocks, [new TableRule(70, 714, 260, 714)]));
    }

    [Fact]
    public void TextInBoxesIsADiagramNotATable()
    {
        var blocks = Table("Table 5 Layers", ["Layer 7", "Application", "Data"], ["Layer 6", "Presentation", "Data"], ["Layer 5", "Session", "Data"]);
        // Each label sits in its own box: vertical rules cross the rows between the columns' words.
        TableRule[] boxes = [new(190, 640, 190, 690), new(250, 640, 250, 690)];

        Assert.DoesNotContain("| --- |", Convert(blocks, boxes));
    }

    [Fact]
    public void ARuledTableIsWrittenOnceAndItsCellsLeaveTheText()
    {
        // A full grid: three columns, three rows.
        TableRule[] grid =
        [
            new(70, 700, 400, 700), new(70, 686, 400, 686), new(70, 672, 400, 672), new(70, 658, 400, 658),
            new(70, 658, 70, 700), new(190, 658, 190, 700), new(320, 658, 320, 700), new(400, 658, 400, 700)
        ];
        var blocks = new List<PdfTextBlock>
        {
            new([Line("Step", 72, 689), Line("Build", 192, 689), Line("Check", 322, 689)]),
            new([Line("1", 72, 675), Line("Cluster", 192, 675), Line("Twelve cells", 322, 675)]),
            new([Line("2", 72, 661), Line("Carve", 192, 661), Line("Five cases", 322, 661)]),
            new([Line("After the table.", 72, 620)])
        };

        string markdown = Convert(blocks, grid);

        Assert.Equal("| Step | Build | Check |\n| --- | --- | --- |\n| 1 | Cluster | Twelve cells |\n| 2 | Carve | Five cases |\n\nAfter the table.\n", markdown);
    }
}
