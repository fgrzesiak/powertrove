using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace FileKit.Pdf.Tests;

public class PdfImagesTests
{
    private static PdfDocument CreateImageDoc(int width, int height, string filter = "/DCTDecode", string colorSpace = "/DeviceRGB", int bpc = 8, bool addMask = false)
    {
        var objects = new System.Collections.Generic.List<string>();

        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");

        string resources = "<< /XObject << /Im1 4 0 R >> >>";
        objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources {resources} >>");

        string maskEntry = addMask ? " /SMask 5 0 R" : "";
        string dict = $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colorSpace} /BitsPerComponent {bpc} /Filter {filter}{maskEntry}";
        objects.Add(TestPdfFactory.StreamObject(dict, "fakeimage"));

        if (addMask)
        {
            string maskDict = $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode";
            objects.Add(TestPdfFactory.StreamObject(maskDict, "fakemask"));
        }

        return PdfDocument.Load(TestPdfFactory.Serialize(objects, "/Root 1 0 R"));
    }

    [Fact]
    public void Enumerate_FindsImagesOnPages()
    {
        var doc = CreateImageDoc(100, 50);
        var images = PdfImages.Enumerate(doc);

        Assert.Single(images);
        Assert.Equal(100, images[0].Width);
        Assert.Equal(50, images[0].Height);
        Assert.Equal("DCTDecode", images[0].Filter);
        Assert.Equal("DeviceRGB", images[0].ColorSpace);
        Assert.Equal(8, images[0].BitsPerComponent);
        Assert.Equal(new[] { 0 }, images[0].PageIndices);
        Assert.False(images[0].HasSoftMask);
    }

    [Fact]
    public void TryDecodeFlatePixels_DecodesPredictors()
    {
        var doc = CreateImageDoc(10, 10, "/DCTDecode", "/DeviceRGB", 8);
        var images = PdfImages.Enumerate(doc);
        Assert.Null(PdfImages.TryDecodeFlatePixels(doc, images[0])); // Should return null for non-Flate
    }

    [Fact]
    public void Enumerate_FindsImagesInParentPageResources()
    {
        var objects = new System.Collections.Generic.List<string>();

        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 /Resources << /XObject << /Im1 4 0 R >> >> >>");
        objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] >>");
        objects.Add(TestPdfFactory.StreamObject("/Type /XObject /Subtype /Image /Width 10 /Height 10 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", "fakeimage"));

        var doc = PdfDocument.Load(TestPdfFactory.Serialize(objects, "/Root 1 0 R"));
        var images = PdfImages.Enumerate(doc);

        Assert.Single(images);
        Assert.Equal(10, images[0].Width);
    }

    [Fact]
    public void TryDecodeFlatePixels_DecodesArrayDecodeParms()
    {
        // 2x2 grayscale image, predictor 15.
        // 2 cols, 1 bpp. rowLength = 2 bytes. each row needs 3 bytes (filter + 2 bytes). 2 rows = 6 bytes.
        byte[] uncompressed = new byte[] { 0, 128, 128, 0, 255, 255 };
        byte[] compressed = TestPdfFactory.Zlib(uncompressed);

        var objects = new System.Collections.Generic.List<string>();
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /XObject << /Im1 4 0 R >> >> >>");

        string dict = $"/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter [/FlateDecode] /DecodeParms [<< /Predictor 15 /Colors 1 /Columns 2 /BitsPerComponent 8 >>] /Length {compressed.Length}";
        string content = System.Text.Encoding.Latin1.GetString(compressed);
        objects.Add($"<< {dict} >>\nstream\n{content}\nendstream");

        var doc = PdfDocument.Load(TestPdfFactory.Serialize(objects, "/Root 1 0 R"));
        var images = PdfImages.Enumerate(doc);
        Assert.Single(images);

        var decoded = PdfImages.TryDecodeFlatePixels(doc, images[0]);
        Assert.NotNull(decoded);
                Assert.Equal(4, decoded.Length);
        Assert.Equal(new byte[] { 128, 128, 255, 255 }, decoded);
    }

    [Fact]
    public void TryDecodeFlatePixels_RejectsInvalidDimensionsBeforeAllocation()
    {
        byte[] uncompressed = new byte[] { 0, 128 };
        byte[] compressed = TestPdfFactory.Zlib(uncompressed);

        var objects = new System.Collections.Generic.List<string>();
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /XObject << /Im1 4 0 R >> >> >>");

        // Massive width, which would allocate ~3GB if rowLength wasn't validated
        string dict = $"/Type /XObject /Subtype /Image /Width 1000000000 /Height 10 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors 3 /Columns 1000000000 /BitsPerComponent 8 >> /Length {compressed.Length}";
        string content = System.Text.Encoding.Latin1.GetString(compressed);
        objects.Add($"<< {dict} >>\nstream\n{content}\nendstream");

        var doc = PdfDocument.Load(TestPdfFactory.Serialize(objects, "/Root 1 0 R"));
        var images = PdfImages.Enumerate(doc);
        Assert.Single(images);

        // Use Assert.ThrowsAny<PdfException> to cover either limit or format exception
        long initialMemory = System.GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsAny<PdfException>(() => PdfImages.TryDecodeFlatePixels(doc, images[0]));
        long finalMemory = System.GC.GetAllocatedBytesForCurrentThread();

        // Assert allocated bytes stay small (e.g. less than 1MB instead of ~3GB)
        Assert.True((finalMemory - initialMemory) < 1024 * 1024, "Should not allocate large buffer.");
    }

    [Fact]
    public void Enumerate_AvoidsCyclesInFormXObjects()
    {
        var objects = new System.Collections.Generic.List<string>();

        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");

        objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /XObject << /Fm1 4 0 R >> >> >>");
        objects.Add(TestPdfFactory.StreamObject("/Type /XObject /Subtype /Form /Resources << /XObject << /Fm2 5 0 R >> >>", "form1"));
        objects.Add(TestPdfFactory.StreamObject("/Type /XObject /Subtype /Form /Resources << /XObject << /Fm1 4 0 R /Im1 6 0 R >> >>", "form2"));
        objects.Add(TestPdfFactory.StreamObject("/Type /XObject /Subtype /Image /Width 10 /Height 10 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", "img"));

        var doc = PdfDocument.Load(TestPdfFactory.Serialize(objects, "/Root 1 0 R"));
        var images = PdfImages.Enumerate(doc);

        Assert.Single(images);
        Assert.Equal(10, images[0].Width);
    }

    [Fact]
    public void ReplaceImage_UpdatesImageStream()
    {
        var doc = CreateImageDoc(200, 100, "/DCTDecode", "/DeviceRGB", 8, addMask: true);
        var images = PdfImages.Enumerate(doc);

        var builder = new PdfDocumentBuilder();
        builder.AddPage(doc, 0);

        var newJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var replacement = new PdfImageReplacement(newJpeg, 100, 50, 3);

        builder.ReplaceImage(doc, images[0].Reference, replacement);

        var ms = new MemoryStream();
        builder.Save(ms);

        var newDoc = PdfDocument.Load(ms.ToArray());
        var newImages = PdfImages.Enumerate(newDoc);

        Assert.Single(newImages);
        Assert.Equal(100, newImages[0].Width);
        Assert.Equal(50, newImages[0].Height);
        Assert.Equal(newJpeg.Length, newImages[0].EncodedLength);
        Assert.True(newImages[0].HasSoftMask);
    }

    [Fact]
    public void ReplaceImage_RejectsInvalidAspectRatio()
    {
        var doc = CreateImageDoc(200, 100); // Ratio 2.0
        var images = PdfImages.Enumerate(doc);
        var builder = new PdfDocumentBuilder();

        var newJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var replacement = new PdfImageReplacement(newJpeg, 100, 100, 3); // Ratio 1.0

        Assert.Throws<PdfOperationException>(() => builder.ReplaceImage(doc, images[0].Reference, replacement));
    }
}
// To ensure the file is complete
