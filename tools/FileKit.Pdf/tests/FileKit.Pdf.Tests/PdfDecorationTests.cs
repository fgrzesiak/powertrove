using System;
using System.IO;
using System.Linq;
using System.Threading;
using FileKit.Pdf;
using Xunit;

namespace FileKit.Pdf.Tests;

public class PdfDecorationTests
{
    [Theory]
    [InlineData(0, PdfAnchor.TopLeft, 20, 20, 20, 80)]
    [InlineData(90, PdfAnchor.TopLeft, 20, 20, 20, 20)]
    [InlineData(180, PdfAnchor.TopLeft, 20, 20, 80, 20)]
    [InlineData(270, PdfAnchor.TopLeft, 20, 20, 80, 80)]
    [InlineData(0, PdfAnchor.BottomRight, 20, 20, 80, 20)]
    [InlineData(90, PdfAnchor.BottomRight, 20, 20, 80, 80)]
    [InlineData(180, PdfAnchor.BottomRight, 20, 20, 20, 80)]
    [InlineData(270, PdfAnchor.BottomRight, 20, 20, 20, 20)]
    public void TestPlacementMatrix(int rotation, PdfAnchor anchor, double objW, double objH, double expectedX, double expectedY)
    {
        // page is 100x100
        var (a, b, c, d, e, f) = PdfPlacement.GetMatrix(0, 0, 100, 100, rotation, anchor, objW, objH, 10, 0);

        // expectedX, expectedY represent the center of the object
        double cx = e + objW/2 * a + objH/2 * c;
        double cy = f + objW/2 * b + objH/2 * d;

        Assert.Equal(expectedX, cx, 3);
        Assert.Equal(expectedY, cy, 3);
    }

    [Fact]
    public void TestTextWidthCalculation()
    {
        Assert.Throws<PdfOperationException>(() => PdfTextMeasurement.GetTextWidth("Hello \u0414", PdfStandardFont.Helvetica, 12));

        double spaceWidth = PdfTextMeasurement.GetTextWidth(" ", PdfStandardFont.Helvetica, 10);
        Assert.Equal(2.78, spaceWidth, 2);
    }


    [Fact]
    public void TestNonZeroOrigin()
    {
        // visibleBox X=10, Y=20, W=100, H=200
        // rotation 0, BottomLeft, margin 0
        var (a, b, c, d, e, f) = PdfPlacement.GetMatrix(10, 20, 100, 200, 0, PdfAnchor.BottomLeft, 50, 50, 0, 0);
        Assert.Equal(10, e, 3);
        Assert.Equal(20, f, 3);
    }

    [Fact]
    public void TestDecorationApplication()
    {
        var docBuilder = new PdfDocumentBuilder();
        docBuilder.AddBlankPage(100, 100);
        using var ms = new MemoryStream();
        docBuilder.Save(ms);
        var doc = PdfDocument.Load(ms.ToArray(), new PdfReadLimits());

        var decBuilder = new PdfDocumentBuilder();
        var decs = new PdfDecoration[]
        {
            new PdfTextDecoration("Hello", PdfAnchor.Center, 12, new PdfColor(0, 0, 0))
        };
        decBuilder.AddPage(doc, 0, 0, decs);

        using var ms2 = new MemoryStream();
        decBuilder.Save(ms2);

        var decDoc = PdfDocument.Load(ms2.ToArray(), new PdfReadLimits());
        Assert.Single(decDoc.Pages);
        var contents = decDoc.Pages[0].Dictionary["Contents"] as PdfArray;
        Assert.NotNull(contents);

        // original was a single stream (null or stream, blank page has null Contents or empty? Actually BlankPage produces no Contents)
        // Wait, BuildBlankPage doesn't emit Contents. Let's see what happens.

        // We just verify it parses and has multiple content streams.
        Assert.True(contents.Items.Count >= 3);

        // And the last one is the decoration
        var lastStreamRef = contents.Items.Last() as PdfReference;
        var lastStream = decDoc.GetObject(lastStreamRef!.ObjectNumber) as PdfStream;
        Assert.NotNull(lastStream);
        string streamData = System.Text.Encoding.ASCII.GetString(lastStream!.Data);
        // It's encoded as hex now, so we look for the hex encoding of "Hello"
        Assert.Contains("48656C6C6F", streamData, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FKFont1", streamData);

        var res = decDoc.Pages[0].Dictionary["Resources"] as PdfDictionary;
        var fontDict = res!["Font"] as PdfDictionary;
        Assert.True(fontDict!.Entries.Any(e => e.Key == "FKFont1"));
    }
    [Fact]
    public void TestImageDecoration()
    {
        var docBuilder = new PdfDocumentBuilder();
        docBuilder.AddBlankPage(100, 100);
        using var ms = new MemoryStream();
        docBuilder.Save(ms);
        var doc = PdfDocument.Load(ms.ToArray(), new PdfReadLimits());

        // create a dummy image
        var image = PdfImage.Load(new byte[] {
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
            0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x00, 0x00, 0x00, 0x00, 0x3a, 0x7e, 0x9b, 0x55,
            0x00, 0x00, 0x00, 0x0a, 0x49, 0x44, 0x41, 0x54,
            0x78, 0x9c, 0x63, 0x62, 0x00, 0x00, 0x00, 0x06, 0x00, 0x03,
            0x36, 0x37, 0x7c, 0xa8, 0x00, 0x00, 0x00, 0x00,
            0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82
        }, new PdfImageLimits());

        var decBuilder = new PdfDocumentBuilder();
        var decs = new PdfDecoration[]
        {
            new PdfImageDecoration(image, PdfAnchor.Center, 50)
        };
        decBuilder.AddPage(doc, 0, 0, decs);

        using var ms2 = new MemoryStream();
        decBuilder.Save(ms2);

        var decDoc = PdfDocument.Load(ms2.ToArray(), new PdfReadLimits());
        var contents = decDoc.Pages[0].Dictionary["Contents"] as PdfArray;
        Assert.NotNull(contents);

        var lastStreamRef = contents.Items.Last() as PdfReference;
        var lastStream = decDoc.GetObject(lastStreamRef!.ObjectNumber) as PdfStream;
        string streamData = System.Text.Encoding.ASCII.GetString(lastStream!.Data);
        Assert.Contains("FKIm1", streamData);

        var res = decDoc.Pages[0].Dictionary["Resources"] as PdfDictionary;
        var xobjDict = res!["XObject"] as PdfDictionary;
        Assert.True(xobjDict!.Entries.Any(e => e.Key == "FKIm1"));
    }


    [Fact]
    public void TestMutation()
    {
        var docBuilder = new PdfDocumentBuilder();
        docBuilder.AddBlankPage(100, 100);
        using var ms = new MemoryStream();
        docBuilder.Save(ms);
        var doc = PdfDocument.Load(ms.ToArray(), new PdfReadLimits());

        var decBuilder = new PdfDocumentBuilder();
        var decs = new PdfDecoration[]
        {
            new PdfTextDecoration("Hello", PdfAnchor.Center, 12, new PdfColor(0, 0, 0))
        };
        decBuilder.AddPage(doc, 0, 0, decs);

        using var ms2 = new MemoryStream();
        decBuilder.Save(ms2);

        var bytes = ms2.ToArray();
        // Mutation test: ensure that the decorated output re-parses and we can mutate it if needed,
        // or just verify that the parser can read everything without throwing.
        var reloaded = PdfDocument.Load(bytes, new PdfReadLimits());
        Assert.Single(reloaded.Pages);
    }


    [Fact]
    public void TestInvisibleAndScaledText()
    {
        var docBuilder = new PdfDocumentBuilder();
        docBuilder.AddBlankPage(100, 100);
        using var ms = new MemoryStream();
        docBuilder.Save(ms);
        var doc = PdfDocument.Load(ms.ToArray(), new PdfReadLimits());

        var decBuilder = new PdfDocumentBuilder();
        var decs = new PdfDecoration[]
        {
            new PdfTextDecoration("Hello", PdfAnchor.Center, 12, new PdfColor(0, 0, 0), Invisible: true, HorizontalScaling: 150.5)
        };
        decBuilder.AddPage(doc, 0, 0, decs);

        using var ms2 = new MemoryStream();
        decBuilder.Save(ms2);

        var decDoc = PdfDocument.Load(ms2.ToArray(), new PdfReadLimits());
        var contents = decDoc.Pages[0].Dictionary["Contents"] as PdfArray;
        var lastStreamRef = contents!.Items.Last() as PdfReference;
        var lastStream = decDoc.GetObject(lastStreamRef!.ObjectNumber) as PdfStream;
        string streamData = System.Text.Encoding.ASCII.GetString(lastStream!.Data);

        Assert.Contains("3 Tr", streamData);
        Assert.Contains("150.5 Tz", streamData);
    }

    [Fact]
    public void TestNonWinAnsiThrows()
    {
        var textDec = new PdfTextDecoration("Hello \u0414", PdfAnchor.Center, 12, new PdfColor(0, 0, 0));
        var docBuilder = new PdfDocumentBuilder();
        docBuilder.AddBlankPage(100, 100);
        using var ms = new MemoryStream();
        docBuilder.Save(ms);
        var doc = PdfDocument.Load(ms.ToArray(), new PdfReadLimits());

        var decBuilder = new PdfDocumentBuilder();
        decBuilder.AddPage(doc, 0, 0, new[] { textDec });

        using var ms2 = new MemoryStream();
        var ex = Assert.Throws<PdfOperationException>(() => decBuilder.Save(ms2));
        Assert.Equal("The text contains characters that cannot be written with the built-in fonts.", ex.Message);
    }
}
