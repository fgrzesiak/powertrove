using System;
using System.IO;
using System.Linq;
using Xunit;
using FileKit.Pdf;

namespace FileKit.Pdf.Tests;

public class PdfOperationAdditionalTests
{
    private static PdfDocument CreateTestDocument()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddBlankPage(612, 792);
        var ms = new MemoryStream();
        builder.Save(ms);
        ms.Position = 0;
        return PdfDocument.Load(ms.ToArray());
    }

    [Fact]
    public void Crop_SetsCropBoxInsideMediaBox()
    {
        var doc = CreateTestDocument();
        using var ms = new MemoryStream();
        PdfTools.Crop(doc, 10, null, ms);
        ms.Position = 0;

        var croppedDoc = PdfDocument.Load(ms.ToArray());
        var page = croppedDoc.Pages[0];

        var cropBox = croppedDoc.Resolve(page.CropBox) as PdfArray;
        Assert.NotNull(cropBox);

        double[] c = cropBox.Items.Select(item => croppedDoc.Resolve(item) switch
        {
            PdfInteger n => (double)n.Value,
            PdfReal r => r.Value,
            _ => 0d
        }).ToArray();

        Assert.Equal(4, c.Length);

        // 10mm = 28.346... points
        double expectedMargin = 10.0 * 72.0 / 25.4;

        Assert.InRange(c[0], expectedMargin - 0.1, expectedMargin + 0.1);
        Assert.InRange(c[1], expectedMargin - 0.1, expectedMargin + 0.1);
        Assert.InRange(c[2], 612 - expectedMargin - 0.1, 612 - expectedMargin + 0.1);
        Assert.InRange(c[3], 792 - expectedMargin - 0.1, 792 - expectedMargin + 0.1);
    }

    [Fact]
    public void ResizePages_A4ToLetter_CentersAndScales()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddBlankPage(595.28, 841.89); // A4
        var msDoc = new MemoryStream();
        builder.Save(msDoc);
        msDoc.Position = 0;
        var doc = PdfDocument.Load(msDoc.ToArray());

        using var ms = new MemoryStream();
        PdfTools.ResizePages(doc, "letter", "fit", ms);
        ms.Position = 0;

        var resizedDoc = PdfDocument.Load(ms.ToArray());
        var (w, h) = PdfTools.GetPageSize(resizedDoc, 0);

        Assert.InRange(w, 611.9, 612.1);
        Assert.InRange(h, 791.9, 792.1);
    }

    [Fact]
    public void ResizePages_FitShrink_DoesNotEnlarge()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddBlankPage(300, 400); // smaller than letter
        var msDoc = new MemoryStream();
        builder.Save(msDoc);
        msDoc.Position = 0;
        var doc = PdfDocument.Load(msDoc.ToArray());

        using var ms = new MemoryStream();
        // fit-shrink shouldn't enlarge, so content stays 300x400 but page is Letter
        PdfTools.ResizePages(doc, "letter", "fit-shrink", ms);
        ms.Position = 0;

        var resizedDoc = PdfDocument.Load(ms.ToArray());
        var (w, h) = PdfTools.GetPageSize(resizedDoc, 0);

        Assert.InRange(w, 611.9, 612.1);
        Assert.InRange(h, 791.9, 792.1);

        // We'd have to inspect the content stream or matrix to ensure scale = 1
        var page = resizedDoc.Pages[0];
        var contents = resizedDoc.Resolve(page.Dictionary["Contents"]) as PdfStream;
        Assert.NotNull(contents);
        var contentStr = System.Text.Encoding.ASCII.GetString(contents.Data);
        // scale 1 means a=1, b=0, c=0, d=1
        Assert.Contains("1 0 0 1", contentStr);
    }
}
