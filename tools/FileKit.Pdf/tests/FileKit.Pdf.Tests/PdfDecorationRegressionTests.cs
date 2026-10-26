using System;
using System.IO;
using System.Linq;
using System.Text;
using FileKit.Pdf;
using Xunit;
using System.Collections.Generic;

namespace FileKit.Pdf.Tests;

public class PdfDecorationRegressionTests
{
    [Fact]
    public void Decorations_OnPageWithIndirectResources_PreservesOriginals()
    {
        // Issue 1: an indirect /Resources is copied with ObjectCopier.CopyValue, which remaps instead of resolving;
        // the 'is PdfDictionary' check then fails and the page gets an EMPTY resource dictionary, so original fonts disappear.

        var originalPdf = CreateTestPdfWithIndirectResources();
        var doc = PdfDocument.Load(originalPdf, new PdfReadLimits());

        var decBuilder = new PdfDocumentBuilder();
        var decs = new PdfDecoration[] { new PdfTextDecoration("Test", PdfAnchor.Center, 12, new PdfColor(0, 0, 0)) };
        decBuilder.AddPage(doc, 0, 0, decs);

        using var ms = new MemoryStream();
        decBuilder.Save(ms);

        var decDoc = PdfDocument.Load(ms.ToArray(), new PdfReadLimits());
        var res = decDoc.Pages[0].Dictionary["Resources"] as PdfDictionary;

        Assert.NotNull(res);
        Assert.True(res.Entries.Any(e => e.Key == "Font"), "Resources should contain Font");
        // Note: Our fix changes indirect resource entries (like /Font) into direct dictionaries
        // inside the direct /Resources dictionary to safely mutate them.
        var fontDict = res["Font"] as PdfDictionary;
        if (fontDict == null)
        {
            var fontDictRef = res["Font"] as PdfReference;
            if (fontDictRef != null) fontDict = decDoc.GetObject(fontDictRef.ObjectNumber) as PdfDictionary;
        }
        Assert.NotNull(fontDict);
        Assert.True(fontDict.Entries.Any(e => e.Key == "F1"), "Font dictionary should contain F1");

        // Also check if FKFont1 is present
        Assert.True(fontDict.Entries.Any(e => e.Key == "FKFont1"), "Font dictionary should contain FKFont1");
    }

    [Fact]
    public void Decorations_OnPageWithIndirectContentsArray_AppendsProperly()
    {
        // Issue 2: /Contents may be a reference to an ARRAY of streams;
        // appending that reference as one element creates a nested array.
        // We simulate a document that has a page with an indirect /Contents array.
        var originalPdf = CreateTestPdfWithIndirectContentsArray();
        var doc = PdfDocument.Load(originalPdf, new PdfReadLimits());

        var decBuilder = new PdfDocumentBuilder();
        var decs = new PdfDecoration[] { new PdfTextDecoration("Test", PdfAnchor.Center, 12, new PdfColor(0, 0, 0)) };
        decBuilder.AddPage(doc, 0, 0, decs);

        using var ms = new MemoryStream();
        decBuilder.Save(ms);

        var decDoc = PdfDocument.Load(ms.ToArray(), new PdfReadLimits());
        var contents = decDoc.Pages[0].Dictionary["Contents"] as PdfArray;

        Assert.NotNull(contents);
        // Ensure no nested arrays.
        foreach (var item in contents.Items)
        {
            Assert.False(item is PdfArray, "Contents array should not contain nested arrays");
        }
    }

    [Fact]
    public void Decorations_OnPageWithIndirectBoxes_ResolvesBeforeCasting()
    {
        // Issue 3: /MediaBox and /CropBox may be indirect; resolve them before computing placement.
        var originalPdf = CreateTestPdfWithIndirectBoxes();
        var doc = PdfDocument.Load(originalPdf, new PdfReadLimits());

        var decBuilder = new PdfDocumentBuilder();
        var decs = new PdfDecoration[] { new PdfTextDecoration("Test", PdfAnchor.Center, 12, new PdfColor(0, 0, 0)) };
        decBuilder.AddPage(doc, 0, 0, decs);

        using var ms = new MemoryStream();
        decBuilder.Save(ms);

        var decDoc = PdfDocument.Load(ms.ToArray(), new PdfReadLimits());
        var overlayStream = GetLastContentStream(decDoc, 0);
        string overlayText = Encoding.ASCII.GetString(overlayStream.Data);
        // Look for the ASCII representation if "Test" isn't found
        Assert.Contains("54657374", overlayText); // Hex encoding for "Test"
    }

    [Fact]
    public void Decorations_WinAnsiText_EncodesCorrectly()
    {
        // Issue 4: WinAnsi text is serialized with Encoding.ASCII, turning every byte above 127 into '?'.
        var docBuilder = new PdfDocumentBuilder();
        docBuilder.AddBlankPage(100, 100);
        using var ms0 = new MemoryStream();
        docBuilder.Save(ms0);
        var doc = PdfDocument.Load(ms0.ToArray(), new PdfReadLimits());

        var decBuilder = new PdfDocumentBuilder();
        var decs = new PdfDecoration[] { new PdfTextDecoration("éü€–", PdfAnchor.Center, 12, new PdfColor(0, 0, 0)) };
        decBuilder.AddPage(doc, 0, 0, decs);

        using var ms = new MemoryStream();
        decBuilder.Save(ms);

        var decDoc = PdfDocument.Load(ms.ToArray(), new PdfReadLimits());
        var overlayStream = GetLastContentStream(decDoc, 0);

        // It should be formatted as <E9FC8096> instead of byte sequence.
        string ascii = Encoding.ASCII.GetString(overlayStream.Data);
        Assert.Contains("<E9FC8096>", ascii);
    }

    [Fact]
    public void Decorations_ResourceNameCollision_Resolved()
    {
        var docBuilder = new PdfDocumentBuilder();
        docBuilder.AddBlankPage(100, 100);
        using var ms0 = new MemoryStream();
        docBuilder.Save(ms0);
        var doc = PdfDocument.Load(ms0.ToArray(), new PdfReadLimits());

        var decBuilder = new PdfDocumentBuilder();
        var decs = new PdfDecoration[] { new PdfTextDecoration("Test", PdfAnchor.Center, 12, new PdfColor(0, 0, 0)) };
        decBuilder.AddPage(doc, 0, 0, decs);

        using var ms1 = new MemoryStream();
        decBuilder.Save(ms1);

        var docWithDec = PdfDocument.Load(ms1.ToArray(), new PdfReadLimits());

        // Now decorate again. The existing page already has FKFont1, FKGs1.
        var decBuilder2 = new PdfDocumentBuilder();
        decBuilder2.AddPage(docWithDec, 0, 0, decs);

        using var ms2 = new MemoryStream();
        decBuilder2.Save(ms2);

        var finalDoc = PdfDocument.Load(ms2.ToArray(), new PdfReadLimits());
        var res = finalDoc.Pages[0].Dictionary["Resources"] as PdfDictionary;
        var fontDict = res?["Font"] as PdfDictionary;

        Assert.NotNull(fontDict);
        Assert.True(fontDict.Entries.Any(e => e.Key == "FKFont1"));
        Assert.True(fontDict.Entries.Any(e => e.Key == "FKFont2"));
    }

    private PdfStream GetLastContentStream(PdfDocument doc, int pageIndex)
    {
        var contents = doc.Pages[pageIndex].Dictionary["Contents"] as PdfArray;
        var lastRef = contents!.Items.Last() as PdfReference;
        return doc.GetObject(lastRef!.ObjectNumber) as PdfStream;
    }

    private bool FindSequence(byte[] data, byte[] sequence)
    {
        for (int i = 0; i <= data.Length - sequence.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < sequence.Length; j++)
            {
                if (data[i + j] != sequence[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }

    private byte[] CreateTestPdfWithIndirectResources()
    {
        string pdf = @"%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Count 1 /Kids [3 0 R] >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox 4 0 R /Contents 5 0 R /Resources 6 0 R >>
endobj
4 0 obj
[ 0 0 612 792 ]
endobj
5 0 obj
<< /Length 12 >>
stream
q
100 100 m
endstream
endobj
6 0 obj
<< /Font 7 0 R /XObject 8 0 R /ExtGState 9 0 R >>
endobj
7 0 obj
<< /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>
endobj
8 0 obj
<< /Im1 << /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 1 >> >>
stream
xff
endstream
endobj
9 0 obj
<< /GS1 << /Type /ExtGState /ca 0.5 >> >>
endobj
xref
0 10
0000000000 65535 f
0000000009 00000 n
0000000058 00000 n
0000000115 00000 n
0000000213 00000 n
0000000245 00000 n
0000000316 00000 n
0000000382 00000 n
0000000454 00000 n
0000000593 00000 n
trailer
<< /Size 10 /Root 1 0 R >>
startxref
644
%%EOF";
        return Encoding.ASCII.GetBytes(pdf.Replace("\r\n", "\n"));
    }

    private byte[] CreateTestPdfWithIndirectContentsArray()
    {
        string pdf = @"%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Count 1 /Kids [3 0 R] >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>
endobj
4 0 obj
[ 5 0 R 6 0 R ]
endobj
5 0 obj
<< /Length 12 >>
stream
q
100 100 m
endstream
endobj
6 0 obj
<< /Length 12 >>
stream
200 200 l
Q
endstream
endobj
xref
0 7
0000000000 65535 f
0000000009 00000 n
0000000058 00000 n
0000000115 00000 n
0000000204 00000 n
0000000236 00000 n
0000000307 00000 n
trailer
<< /Size 7 /Root 1 0 R >>
startxref
380
%%EOF";
        return Encoding.ASCII.GetBytes(pdf.Replace("\r\n", "\n"));
    }

    private byte[] CreateTestPdfWithIndirectBoxes()
    {
        string pdf = @"%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Count 1 /Kids [3 0 R] >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox 4 0 R /CropBox 5 0 R /Contents 6 0 R >>
endobj
4 0 obj
[ 0 0 100 100 ]
endobj
5 0 obj
[ 10 10 90 90 ]
endobj
6 0 obj
<< /Length 12 >>
stream
q
100 100 m
endstream
endobj
xref
0 7
0000000000 65535 f
0000000009 00000 n
0000000058 00000 n
0000000115 00000 n
0000000213 00000 n
0000000245 00000 n
0000000277 00000 n
trailer
<< /Size 7 /Root 1 0 R >>
startxref
348
%%EOF";
        return Encoding.ASCII.GetBytes(pdf.Replace("\r\n", "\n"));
    }
}
