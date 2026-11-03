namespace FileKit.Pdf.Tests;

/// <summary>Link annotations and destinations as the layout reader exposes them.</summary>
public sealed class PdfLinksTests
{
    private static PdfDocument Document(string annotations, string catalogEntries = "", params string[] extra) => PdfDocument.Load(TestPdfFactory.Serialize(
    [
        "<< /Type /Catalog /Pages 2 0 R " + catalogEntries + " >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Contents 5 0 R /Annots [" + annotations + "] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Contents 5 0 R >>",
        TestPdfFactory.StreamObject("", "q Q"),
        .. extra
    ], "/Root 1 0 R"));

    [Fact]
    public void WebAddressesAndPlacesInTheDocumentAreRead()
    {
        var pdf = Document(
            "<< /Type /Annot /Subtype /Link /Rect [10 20 110 40] /A << /S /URI /URI (https://example.com/) >> >> " +
            "<< /Type /Annot /Subtype /Link /Rect [10 50 110 70] /Dest [4 0 R /XYZ 0 640 0] >> " +
            "<< /Type /Annot /Subtype /Link /Rect [10 80 110 100] /A << /S /URI /URI (javascript:alert(1)) >> >> " +
            "<< /Type /Annot /Subtype /Text /Rect [10 110 110 130] >>");

        var links = PdfLayoutReader.Read(pdf).Pages[0].Links!;

        Assert.Equal(2, links.Count);
        Assert.Equal("https://example.com/", links[0].Uri);
        Assert.Equal((10, 20, 110, 40), (links[0].Left, links[0].Bottom, links[0].Right, links[0].Top));
        Assert.Equal(new PdfDestination(1, 640), links[1].Target);
    }

    [Fact]
    public void NamedDestinationsResolveThroughTheNameTree()
    {
        var pdf = Document("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /GoTo /D (chapter) >> >>",
            "/Names << /Dests 6 0 R >>",
            "<< /Kids [7 0 R] >>",
            "<< /Limits [(a) (z)] /Names [(chapter) << /D [4 0 R /FitH 500] >>] >>");

        var link = Assert.Single(PdfLayoutReader.Read(pdf).Pages[0].Links!);

        Assert.Equal(new PdfDestination(1, 500), link.Target);
    }

    [Fact]
    public void BookmarksCarryTheTopOfTheirDestination()
    {
        var pdf = Document("", "/Outlines 6 0 R",
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R >>",
            "<< /Title (Intro) /Parent 6 0 R /Dest [4 0 R /XYZ 0 700 0] >>");

        var item = Assert.Single(PdfOutline.Read(pdf));

        Assert.Equal(("Intro", 1, 700.0), (item.Title, item.PageIndex, item.Top));
    }
}
