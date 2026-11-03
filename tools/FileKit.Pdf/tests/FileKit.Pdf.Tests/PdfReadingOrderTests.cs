using System.Text;

namespace FileKit.Pdf.Tests;

/// <summary>
/// Content streams as real PDFs write them (split content arrays, inline images, scaled text matrices, one-byte CID
/// codes) and the grouping of words, lines and columns. Helvetica without Widths advances half a font size per glyph.
/// </summary>
public sealed class PdfReadingOrderTests
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

    private static PdfLayoutPage Page(string content, string font = Helvetica, params string[] extra) =>
        Assert.Single(PdfLayoutReader.Read(PdfDocument.Load(TestPdfFactory.Serialize(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Contents 5 0 R /Resources << /Font << /F1 4 0 R >> >> >>",
            font,
            TestPdfFactory.StreamObject("", content),
            .. extra
        ], "/Root 1 0 R"))).Pages);

    private static string[] Words(PdfLayoutPage page) =>
        page.Blocks.SelectMany(b => b.Lines).SelectMany(l => l.Words).Select(w => w.Text).ToArray();

    [Fact]
    public void ContentArrayPartsAreReadAsOneStream()
    {
        var pdf = PdfDocument.Load(TestPdfFactory.Serialize(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Contents [5 0 R 6 0 R] /Resources << /Font << /F1 4 0 R >> >> >>",
            Helvetica,
            TestPdfFactory.StreamObject("", "BT /F1 12 Tf 20 700 Td [(Split) -50"),
            TestPdfFactory.StreamObject("", "(Array)] TJ ET")
        ], "/Root 1 0 R"));

        Assert.Equal(["SplitArray"], Words(Assert.Single(PdfLayoutReader.Read(pdf).Pages)));
    }

    [Fact]
    public void InlineImageDataIsSkipped()
    {
        var page = Page("BI /W 2 /H 1 /BPC 8 /CS /G ID (]>> \n EI BT /F1 12 Tf 20 700 Td (After) Tj ET");

        Assert.Equal(["After"], Words(page));
    }

    [Fact]
    public void FontSizeIsTheDrawnSizeIncludingTheTextMatrix()
    {
        var page = Page("BT /F1 1 Tf 10 0 0 10 20 700 Tm (Scaled) Tj ET");

        var word = Assert.Single(page.Blocks.SelectMany(b => b.Lines).SelectMany(l => l.Words));
        Assert.Equal(10, word.FontSize, 1);
        Assert.Equal(30, word.Right - word.Left, 1);
    }

    [Fact]
    public void OneByteCodespaceOfAnEmbeddedCMapDecodesSingleBytes()
    {
        string font = "<< /Type /Font /Subtype /Type0 /BaseFont /OneByte /Encoding 6 0 R /ToUnicode 7 0 R " +
                      "/DescendantFonts [<< /Subtype /CIDFontType0 /DW 500 >>] >>";
        string encoding = TestPdfFactory.StreamObject("/Type /CMap",
            "begincmap 1 begincodespacerange <00> <FF> endcodespacerange 1 begincidrange <00> <FF> 0 endcidrange endcmap");
        string toUnicode = TestPdfFactory.StreamObject("",
            "begincmap 1 beginbfrange <41> <43> <0041> endbfrange endcmap");

        var page = Page("BT /F1 12 Tf 20 700 Td <414243> Tj ET", font, encoding, toUnicode);

        Assert.Equal(["ABC"], Words(page));
        Assert.Equal(18, page.Glyphs[^1].Right - page.Glyphs[0].Left, 1);
    }

    [Fact]
    public void ASpaceDrawnOverThePreviousGlyphDoesNotSplitTheWord()
    {
        // "de" ends at 10; the space starts 3 points back inside it, and "ne" continues right after "de".
        var page = Page("BT /F1 10 Tf 0 700 Td [(de) 300 ( ) 200 (ne)] TJ ET");

        Assert.Equal(["dene"], Words(page));
    }

    [Fact]
    public void WordsSeparatedOnlyByPositionAreSplit()
    {
        var page = Page("BT /F1 10 Tf 20 700 Td (has) Tj 17 0 Td (two) Tj ET");

        Assert.Equal(["has", "two"], Words(page));
    }

    [Fact]
    public void JustifiedGapsStayInTheLineWhileTheGutterSplitsColumns()
    {
        // 48 glyphs fill a column from x 40 to 280; the right column starts at 320.
        string full = new string('a', 48);
        var content = new StringBuilder("BT /F1 10 Tf ");
        foreach (var (x, y, text) in new[]
        {
            (40, 700, full), (320, 700, "right1"),
            (40, 688, "left"), (260, 688, "wide"), (320, 688, "right2"),
            (40, 676, full), (320, 676, "right3"),
            (40, 664, "end."), (320, 664, "right4")
        })
            content.Append($"1 0 0 1 {x} {y} Tm ({text}) Tj ");
        content.Append("ET");

        var page = Page(content.ToString());

        var words = Words(page);
        Assert.Equal([full, "left", "wide", full, "end.", "right1", "right2", "right3", "right4"], words);
        Assert.Contains(page.Blocks.SelectMany(b => b.Lines), line => line.Words.Select(w => w.Text).SequenceEqual(["left", "wide"]));
    }

    [Fact]
    public void AFullWidthTitleComesBeforeBothColumns()
    {
        var content = "BT /F1 16 Tf 1 0 0 1 40 740 Tm (Title) Tj /F1 10 Tf " +
                      "1 0 0 1 40 700 Tm (L1) Tj 1 0 0 1 320 700 Tm (R1) Tj " +
                      "1 0 0 1 40 688 Tm (L2) Tj 1 0 0 1 320 688 Tm (R2) Tj ET";

        Assert.Equal(["Title", "L1", "L2", "R1", "R2"], Words(Page(content)));
    }
}
