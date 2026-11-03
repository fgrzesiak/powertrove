using System.Text;

namespace FileKit.Pdf.Tests;

/// <summary>Small deterministic PDFs: text matrices, font encodings, columns, rules, forms and raster XObjects.</summary>
public sealed class PdfLayoutReaderTests
{
    private static byte[] Create(string content, string font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        string? image = null, string? form = null, string? cmap = null)
    {
        var page="<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Contents 5 0 R " +
            "/Resources << /Font << /F1 4 0 R >> /XObject << /Im1 6 0 R /Fm1 7 0 R >> >> >>";
        return TestPdfFactory.Serialize([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            page, font,
            TestPdfFactory.StreamObject("", content),
            image ?? TestPdfFactory.StreamObject("/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB", "ABC"),
            form ?? TestPdfFactory.StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 100 100]", "q Q"),
            cmap ?? TestPdfFactory.StreamObject("", "")
        ], "/Root 1 0 R");
    }

    [Fact]
    public void ASmallerRaisedTailOfAWordIsMarkedAsASuperscript()
    {
        var bytes = Create("BT /F1 12 Tf 20 700 Td (frames) Tj /F1 7 Tf 5 Ts (12) Tj /F1 12 Tf 0 Ts ( and) Tj ET " +
            "BT /F1 12 Tf 20 650 Td (H) Tj /F1 7 Tf -2 Ts (2) Tj /F1 12 Tf 0 Ts (O) Tj ET");
        var words = PdfLayoutReader.Read(PdfDocument.Load(bytes)).Pages[0].Blocks.SelectMany(b => b.Lines).SelectMany(l => l.Words).ToList();

        var frames = Assert.Single(words, w => w.Text.StartsWith("frames", StringComparison.Ordinal));
        Assert.Equal("frames12", frames.Text);
        Assert.Equal(6, frames.RaisedFrom);
        Assert.Equal(-1, Assert.Single(words, w => w.Text == "and").RaisedFrom);
        // A lowered subscript is no superscript.
        Assert.Equal(-1, Assert.Single(words, w => w.Text.StartsWith('H')).RaisedFrom);
    }

    [Fact]
    public void TextPositionsAndStrokedRectangleAreExposed()
    {
        var bytes = Create("BT /F1 12 Tf 20 700 Td (Hello World) Tj ET 20 650 100 40 re S");
        var pdf = PdfDocument.Load(bytes);
        var page = Assert.Single(PdfLayoutReader.Read(pdf).Pages);
        Assert.Contains(page.Blocks.SelectMany(b => b.Lines).SelectMany(l => l.Words),
            w => w.Text.Contains("Hello", StringComparison.Ordinal));
        Assert.InRange(page.Glyphs[0].Left, 19, 21);
        Assert.Contains(page.Rules, r => Math.Abs(r.Y1 - 650) < 1 && Math.Abs(r.Y2 - 650) < 1);
        Assert.True(page.Rules.Count >= 4);
    }

    [Fact]
    public void ColumnsAreReadColumnByColumnRatherThanInterleavingRows()
    {
        var bytes = Create(
            "BT /F1 12 Tf 40 700 Td (LEFTTOP) Tj ET " +
            "BT /F1 12 Tf 40 660 Td (LEFTBOTTOM) Tj ET " +
            "BT /F1 12 Tf 360 700 Td (RIGHTTOP) Tj ET " +
            "BT /F1 12 Tf 360 660 Td (RIGHTBOTTOM) Tj ET");
        var page = Assert.Single(PdfLayoutReader.Read(PdfDocument.Load(bytes)).Pages);
        var words = page.Blocks.SelectMany(b => b.Lines).SelectMany(l => l.Words).Select(w => w.Text).ToArray();
        Assert.True(Array.IndexOf(words, "LEFTBOTTOM") < Array.IndexOf(words, "RIGHTTOP"));
    }

    [Fact]
    public void FormXObjectTransformationShiftsTextAndRuling()
    {
        var form = TestPdfFactory.StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /Font << /F1 4 0 R >> >>",
            "BT /F1 12 Tf 10 10 Td (FORM) Tj ET 10 50 m 90 50 l S");
        var bytes = Create("q 1 0 0 1 80 100 cm /Fm1 Do Q", form: form);
        var page = Assert.Single(PdfLayoutReader.Read(PdfDocument.Load(bytes)).Pages);
        Assert.Contains(page.Glyphs, g => g.Text=="F" && g.Left >= 89);
        Assert.Contains(page.Rules, r => r.X1 >= 89 && r.Y1 >= 149);
    }

    [Fact]
    public void DifferencesAndCffLikeType0ToUnicodeDecodeWithoutExternalFontEngine()
    {
        var font="<< /Type /Font /Subtype /Type0 /BaseFont /ExampleCID /Encoding /Identity-H " +
            "/DescendantFonts [<< /Type /Font /Subtype /CIDFontType0 /DW 600 >>] /ToUnicode 8 0 R >>";
        var cmap="1 begincodespacerange <0000> <FFFF> endcodespacerange " +
            "1 beginbfchar <0021> <00C4> endbfchar";
        var bytes = Create("BT /F1 12 Tf 20 700 Td <0021> Tj ET", font: font, cmap: TestPdfFactory.StreamObject("", cmap));
        var page = Assert.Single(PdfLayoutReader.Read(PdfDocument.Load(bytes)).Pages);
        Assert.Contains(page.Glyphs, g => g.Text=="Ä");
    }

    [Fact]
    public void FlateRgbAndSoftMaskProducePositionedAlphaPng()
    {
        byte[] color = TestPdfFactory.Zlib([255, 0, 0, 0, 255, 0]);
        byte[] alpha = TestPdfFactory.Zlib([30, 230]);
        string mask=$"<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray " +
            $"/Filter /FlateDecode /Length {alpha.Length} >>\nstream\n{Encoding.Latin1.GetString(alpha)}\nendstream";
        // The 8th test object is an image soft mask, not a CMap.
        var image=$"<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 " +
            $"/ColorSpace /DeviceRGB /Filter /FlateDecode /SMask 8 0 R /Length {color.Length} >>\nstream\n{Encoding.Latin1.GetString(color)}\nendstream";
        var bytes = Create("q 50 0 0 50 80 200 cm /Im1 Do Q", image: image, cmap: mask);
        var page = Assert.Single(PdfLayoutReader.Read(PdfDocument.Load(bytes),
            new PdfLayoutOptions { IncludeImages = true }).Pages);
        var png = Assert.Single(page.Images);
        Assert.Equal(".png", png.Extension);
        Assert.Equal(80, png.Left);
        Assert.True(png.Bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 }));
        Assert.Equal((byte)6, png.Bytes[25]); // PNG color type RGBA
    }

    [Fact]
    public void OneBitIndexedImageAndLzwGrayImageProducePngs()
    {
        var indexed = TestPdfFactory.StreamObject(
            "/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 1 " +
            "/ColorSpace [/Indexed /DeviceRGB 1 <FF000000FF00>]",
            System.Text.Encoding.Latin1.GetString(new byte[] { 0b0100_0000 }));
        var page = Assert.Single(PdfLayoutReader.Read(PdfDocument.Load(
            Create("q 50 0 0 50 80 200 cm /Im1 Do Q", image: indexed)),
            new PdfLayoutOptions { IncludeImages = true }).Pages);
        Assert.Equal(".png", Assert.Single(page.Images).Extension);

        // Clear=256, literal gray sample=127, EOD=257; 9-bit MSB-first LZW.
        int[] codes = [256, 127, 257];
        var bytes = new byte[4];
        int bit = 0;
        foreach (int code in codes)
            for (int i = 8; i >= 0; i--, bit++)
                if (((code >> i) & 1) != 0) bytes[bit / 8] |= (byte)(1 << (7 - bit % 8));
        var lzw = TestPdfFactory.StreamObject(
            "/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 " +
            "/ColorSpace /DeviceGray /Filter /LZWDecode",
            System.Text.Encoding.Latin1.GetString(bytes));
        page = Assert.Single(PdfLayoutReader.Read(PdfDocument.Load(
            Create("q 50 0 0 50 80 200 cm /Im1 Do Q", image: lzw)),
            new PdfLayoutOptions { IncludeImages = true }).Pages);
        Assert.Equal(".png", Assert.Single(page.Images).Extension);
    }

    [Fact]
    public void WinAnsiBulletAndOtherStandardEncodingsUseCorrectGlyphNames()
    {
        Assert.Equal("•", PdfGlyphNames.FromEncoding("WinAnsiEncoding", 0x95));
        Assert.Equal("•", PdfGlyphNames.FromName("bullet"));
        Assert.Equal("ﬁ", PdfGlyphNames.FromName("fi"));
        Assert.Equal("ﬂ", PdfGlyphNames.FromName("fl"));
        Assert.Equal("Ä", PdfGlyphNames.FromName("Adieresis"));
        Assert.Equal("A", PdfGlyphNames.FromEncoding("StandardEncoding", 65));
        Assert.Equal("Ä", PdfGlyphNames.FromEncoding("MacRomanEncoding", 0x80));

        string font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                      "/Encoding /WinAnsiEncoding >>";
        var page = Assert.Single(PdfLayoutReader.Read(PdfDocument.Load(
            Create("BT /F1 12 Tf 20 700 Td <9520526576656e7565> Tj ET", font: font))).Pages);
        Assert.Contains(page.Blocks.SelectMany(block => block.Lines)
            .SelectMany(line => line.Words), word => word.Text == "•");
        Assert.Contains(page.Blocks.SelectMany(block => block.Lines)
            .SelectMany(line => line.Words), word => word.Text == "Revenue");
    }

    [Fact]
    public void LigaturesRemainWithinWordsAndTjOffsetsCreateWordBoundaries()
    {
        string font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                      "/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [1 /fi /fl] >> >>";
        var page = Assert.Single(PdfLayoutReader.Read(PdfDocument.Load(
            Create("BT /F1 12 Tf 30 700 Td [(Of) 0 <01> 0 (ce) -600 (next)] TJ ET", font: font))).Pages);
        string[] words = page.Blocks.SelectMany(block => block.Lines)
            .SelectMany(line => line.Words).Select(word => word.Text).ToArray();
        Assert.Contains("Ofﬁce", words);
        Assert.Contains("next", words);
    }

    [Fact]
    public void IdentityHWithoutMappingNeverEmitsCharacterCodesAsUnicode()
    {
        var font = "<< /Type /Font /Subtype /Type0 /BaseFont /NoUnicode " +
                   "/Encoding /Identity-H /DescendantFonts " +
                   "[<< /Subtype /CIDFontType2 /DW 500 >>] >>";
        var result = PdfLayoutReader.Read(PdfDocument.Load(
            Create("BT /F1 12 Tf 20 700 Td <00410042> Tj ET", font: font)));
        var page = Assert.Single(result.Pages);
        Assert.Empty(page.Glyphs);
        Assert.True(result.SkippedText >= 2);
    }

    [Fact]
    public void EmbeddedType1EncodingOverridesUnknownBaseCode()
    {
        string encoding = "/FontFile 8 0 R";
        var font = "<< /Type /Font /Subtype /Type1 /BaseFont /Custom " +
                   "/FontDescriptor << /Type /FontDescriptor " + encoding + " >> >>";
        string pfa = "%!PS-AdobeFont-1.0: Custom 1.0\n/Encoding 256 array\n" +
                     "dup 149 /bullet put\neexec\n";
        var pdf = PdfDocument.Load(Create(
            "BT /F1 12 Tf 20 700 Td <95> Tj ET", font: font,
            cmap: TestPdfFactory.StreamObject("", pfa)));
        var page = Assert.Single(PdfLayoutReader.Read(pdf).Pages);
        Assert.Contains(page.Glyphs, glyph => glyph.Text == "•");
    }

    [Fact]
    public void EmbeddedTrueTypeCmapResolvesIdentityCidThroughGlyphId()
    {
        // One Unicode cmap format 4 segment: U+0041 -> GID 1.
        byte[] cmap4 =
        [
            0, 4, 0, 32, 0, 0, 0, 4, 0, 4, 0, 1, 0, 0,
            0, 65, 255, 255, 0, 0, 0, 65, 255, 255,
            255, 192, 0, 1, 0, 0, 0, 0
        ];
        byte[] sfnt =
        [
            0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0,
            99, 109, 97, 112, 0, 0, 0, 0,
            0, 0, 0, 28, 0, 0, 0, 44,
            0, 0, 0, 1, 0, 3, 0, 1, 0, 0, 0, 12,
            .. cmap4
        ];
        static string PdfStream(byte[] payload) =>
            TestPdfFactory.StreamObject("", Encoding.Latin1.GetString(payload));

        var pdf = PdfDocument.Load(TestPdfFactory.Serialize(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 600 800] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Custom /Encoding /Identity-H " +
                "/DescendantFonts [<< /Subtype /CIDFontType2 /DW 500 /CIDToGIDMap 7 0 R " +
                "/FontDescriptor << /FontFile2 6 0 R >> >>] >>",
            TestPdfFactory.StreamObject("", "BT /F1 12 Tf 20 700 Td <0001> Tj ET"),
            PdfStream(sfnt),
            PdfStream([0, 0, 0, 1])
        ], "/Root 1 0 R"));

        var result = PdfLayoutReader.Read(pdf);
        Assert.Contains(Assert.Single(result.Pages).Glyphs, glyph => glyph.Text == "A");
        Assert.Equal(0, result.SkippedText);
    }

    [Fact]
    public void EmbeddedCffCharsetAndEncodingRecoverMissingUnicodeMapping()
    {
        // CFF1: GID 1 has SID 34 (A), built-in encoding code 65 -> GID 1.
        // Top DICT has fixed-width offset operands for charset / encoding / CharStrings.
        byte[] cff =
        [
            1, 0, 4, 4,
            0, 1, 1, 1, 2, 88,
            0, 1, 1, 1, 19,
            29, 0, 0, 0, 37, 15,
            29, 0, 0, 0, 40, 16,
            29, 0, 0, 0, 43, 17,
            0, 0, 0, 0,
            0, 0, 34,
            0, 1, 65,
            0, 2, 1, 1, 2, 3, 14, 14
        ];
        var pdf = PdfDocument.Load(TestPdfFactory.Serialize(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 600 800] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /CustomCff " +
                "/Encoding /FontSpecificEncoding /FontDescriptor << /FontFile3 6 0 R >> >>",
            TestPdfFactory.StreamObject("", "BT /F1 12 Tf 20 700 Td <41> Tj ET"),
            TestPdfFactory.StreamObject("/Subtype /Type1C", Encoding.Latin1.GetString(cff))
        ], "/Root 1 0 R"));

        Assert.Contains(Assert.Single(PdfLayoutReader.Read(pdf).Pages).Glyphs,
            glyph => glyph.Text == "A");
    }

    [Fact]
    public void ContentCancellationAndLetterLimitsAreEnforced()
    {
        var doc = PdfDocument.Load(Create("BT /F1 12 Tf 20 700 Td (HELLO) Tj ET"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => PdfLayoutReader.Read(doc, cancellationToken: cancelled.Token));
        Assert.Throws<PdfLimitExceededException>(() => PdfLayoutReader.Read(doc,
            new PdfLayoutOptions { MaxLetters = 2 }));
    }
}
