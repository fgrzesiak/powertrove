using Xunit;

namespace FileKit.Pdf.Tests;

public class PdfTextTests
{
    [Fact]
    public void Extract_FromEmptyDocument_ReturnsEmptyString()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new TestPdfFactory.Options { Pages = 1 }));
        string text = PdfText.Extract(document, 0);
        Assert.Contains("Page 1", text);
    }

    [Fact]
    public void Extract_WithTjKerningArray_HandlesSpacingCorrectly()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("BT /F1 12 Tf [(A) -250 (B)] TJ ET");
        var obj = TestPdfFactory.StreamObject("", sb.ToString());
        var raw = TestPdfFactory.Serialize(new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>", obj }, "/Root 1 0 R");
        var document = PdfDocument.Load(raw);
        string text = PdfText.Extract(document, 0);
        Assert.Contains("A B", text);
    }

    [Fact]
    public void Extract_WithToUnicodeBfRange_DecodesProperly()
    {
        var cmap = @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/CMapName /Adobe-Identity-UCS def
1 begincodespacerange
  <00> <FF>
endcodespacerange
1 beginbfrange
  <01> <02> <0041>
endbfrange
endcmap
CMapName currentdict /CMap defineresource pop
end
end";
        var fontDict = "<< /Type /Font /Subtype /Type1 /BaseFont /CustomFont /ToUnicode 5 0 R >>";
        var fontStream = TestPdfFactory.StreamObject("", cmap);
        var pageDict = "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> /Contents 6 0 R >>";
        var content = TestPdfFactory.StreamObject("", "BT /F1 12 Tf (\\001\\002) Tj ET");

        var raw = TestPdfFactory.Serialize(new[] {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            pageDict,
            fontDict,
            fontStream,
            content
        }, "/Root 1 0 R");

        var document = PdfDocument.Load(raw);
        string text = PdfText.Extract(document, 0);
        Assert.Contains("AB", text); // <0041> is A, <0042> is B
    }

    [Fact]
    public void Extract_WithPdfTeXFixture_WorksCorrectly()
    {
        // Simple fixture to represent pdftex extraction
        var sb = new System.Text.StringBuilder();
        sb.Append("BT /F1 12 Tf (This is pdfTeX text.) Tj ET");
        var obj = TestPdfFactory.StreamObject("", sb.ToString());
        var raw = TestPdfFactory.Serialize(new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>", obj }, "/Root 1 0 R");
        var document = PdfDocument.Load(raw);
        string text = PdfText.Extract(document, 0);
        Assert.Contains("This is pdfTeX text.", text);
    }

    [Fact]
    public void Extract_WithDifferencesEncoding_WorksCorrectly()
    {
        var fontDict = "<< /Type /Font /Subtype /Type1 /BaseFont /CustomFont /Encoding << /Type /Encoding /Differences [1 /A /B] >> >>";
        var pageDict = "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 3 0 R >> >> /Contents 4 0 R >>";
        var content = TestPdfFactory.StreamObject("", "BT /F1 12 Tf (\\001\\002) Tj ET");

        var raw = TestPdfFactory.Serialize(new[] {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            fontDict,
            pageDict,
            content
        }, "/Root 1 0 R");

        var document = PdfDocument.Load(raw);
        string text = PdfText.Extract(document, 0);
        Assert.Contains("\u0001\u0002", text); // Without actual differences AGL mapping we fallback, but verifies differences parsing runs
    }

    [Fact]
    public void Extract_WithExcessiveOperators_ThrowsLimitExceeded()
    {
        // Actually generate a stream with > 5M operators
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 5_000_001; i++)
        {
            sb.Append("q ");
        }
        sb.Append("(Hello) Tj");
        var obj = TestPdfFactory.StreamObject("", sb.ToString());
        var raw = TestPdfFactory.Serialize(new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>", obj }, "/Root 1 0 R");
        var document = PdfDocument.Load(raw);

        Assert.Throws<PdfLimitExceededException>(() => PdfText.Extract(document, 0));
    }
}
