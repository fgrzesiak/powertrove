namespace FileKit.Pdf.Tests;

/// <summary>Marked content in page content and the structure tree of tagged PDFs.</summary>
public sealed class PdfStructureTests
{
    private static PdfDocument Tagged(string content, string structTree, params string[] extra) => PdfDocument.Load(TestPdfFactory.Serialize(
    [
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Contents 5 0 R " +
            "/Resources << /Font << /F1 4 0 R >> /Properties << /MC9 << /MCID 9 >> >> >> >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        TestPdfFactory.StreamObject("", content),
        structTree,
        .. extra
    ], "/Root 1 0 R"));

    [Fact]
    public void WordsCarryTheirMarkedContentAndArtifactsAreFlagged()
    {
        var pdf = Tagged(
            "/Artifact BMC BT /F1 10 Tf 20 780 Td (Header) Tj ET EMC " +
            "/P << /MCID 0 >> BDC BT /F1 10 Tf 20 700 Td (One) Tj ET EMC " +
            "/P /MC9 BDC BT /F1 10 Tf 20 680 Td (Two) Tj ET EMC",
            "<< /Type /StructTreeRoot /K [] >>");

        var words = PdfLayoutReader.Read(pdf).Pages[0].Blocks.SelectMany(b => b.Lines).SelectMany(l => l.Words).ToList();

        Assert.Equal(("Header", (int?)null, true), (words[0].Text, words[0].Mcid, words[0].Artifact));
        Assert.Equal(("One", (int?)0, false), (words[1].Text, words[1].Mcid, words[1].Artifact));
        Assert.Equal(("Two", (int?)9, false), (words[2].Text, words[2].Mcid, words[2].Artifact));
    }

    [Fact]
    public void AWordEndsWhereItsMarkedContentChanges()
    {
        var pdf = Tagged("BT /F1 10 Tf 20 700 Td /Span << /MCID 1 >> BDC (Foot) Tj EMC /Span << /MCID 2 >> BDC (note) Tj EMC ET",
            "<< /Type /StructTreeRoot /K [] >>");

        var words = PdfLayoutReader.Read(pdf).Pages[0].Blocks.SelectMany(b => b.Lines).SelectMany(l => l.Words).Select(w => (w.Text, w.Mcid)).ToList();

        Assert.Equal([("Foot", (int?)1), ("note", (int?)2)], words);
    }

    [Fact]
    public void TheTreeFollowsTheRoleMapAndReadsContentSpansAndAlternativeText()
    {
        var pdf = Tagged("q Q",
            "<< /Type /StructTreeRoot /RoleMap << /Heading1 /H1 /Body /P >> /K 7 0 R >>",
            "<< /S /Document /P 6 0 R /Pg 3 0 R /K [" +
                "<< /S /Heading1 /K 0 >> " +
                "<< /S /Body /K << /Type /MCR /Pg 3 0 R /MCID 4 >> >> " +
                "<< /S /Table /K [<< /S /TR /K [<< /S /TH /A << /O /Table /ColSpan 2 >> /K 5 >>] >>] >> " +
                "<< /S /Figure /Alt (A chart) /K 6 >> " +
                "<< /Type /OBJR /Obj 4 0 R >>] >>");

        var document = Assert.Single(PdfStructure.Read(pdf));
        var children = document.Children;

        Assert.Equal(["H1", "P", "Table", "Figure"], children.Select(c => c.Type));
        Assert.Equal([new PdfMarkedContent(0, 0)], children[0].Content);
        Assert.Equal([new PdfMarkedContent(0, 4)], children[1].Content);
        Assert.Equal(2, children[2].Children[0].Children[0].ColumnSpan);
        Assert.Equal("A chart", children[3].Alt);
    }

    [Fact]
    public void CyclesInTheTreeEndInsteadOfLooping()
    {
        var pdf = Tagged("q Q", "<< /Type /StructTreeRoot /K 7 0 R >>", "<< /S /Sect /Pg 3 0 R /K [7 0 R 1] >>");

        var root = Assert.Single(PdfStructure.Read(pdf));

        Assert.Empty(root.Children);
        Assert.Equal([new PdfMarkedContent(0, 1)], root.Content);
    }
}
