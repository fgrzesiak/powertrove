using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileKit.Pdf.Tests;

public class PdfImageExtractionTests
{
    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("gray.jpg")]
    public void JpegExtraction_PreservesEveryEncodedByte(string fixture)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        using var pdf = new MemoryStream();
        PdfTools.ImagesToPdf(new[] { PdfImage.Load(original) }, pdf);
        var document = PdfDocument.Load(pdf.ToArray());

        var extracted = PdfImageExtractor.TryExtract(document, Assert.Single(PdfImages.Enumerate(document)));

        Assert.NotNull(extracted);
        Assert.Equal(".jpg", extracted.Extension);
        Assert.Equal(original, extracted.Data);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void PngWriter_HasValidChunksAndExactPixels(int components)
    {
        byte[] pixels = Enumerable.Range(0, 7 * 5 * components).Select(i => (byte)(i * 7)).ToArray();
        using var output = new MemoryStream();
        PdfPngWriter.Write(output, 7, 5, components, pixels);
        Assert.True(output.CanWrite);
        AssertPng(output.ToArray(), 7, 5, components, pixels);
    }

    [Fact]
    public void PngWriter_SplitsLargeCompressedDataIntoValidIdatChunks()
    {
        byte[] pixels = new byte[512 * 128 * 3];
        new Random(111).NextBytes(pixels);
        using var output = new MemoryStream();
        PdfPngWriter.Write(output, 512, 128, 3, pixels);
        byte[] png = output.ToArray();
        int chunks = 0;
        for (int offset = 8; offset < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (Encoding.ASCII.GetString(png, offset + 4, 4) == "IDAT") { chunks++; Assert.InRange(length, 1, 65_536); }
            offset += length + 12;
        }
        Assert.True(chunks > 1);
        AssertPng(png, 512, 128, 3, pixels);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void FlateExtraction_ProducesIndependentPngWithExactPixels(int components)
    {
        byte[] pixels = Enumerable.Range(0, 4 * 3 * components).Select(i => (byte)(i * 9)).ToArray();
        var document = ImageDocument(4, 3, components, pixels);

        var extracted = PdfImageExtractor.TryExtract(document, Assert.Single(PdfImages.Enumerate(document)));

        Assert.NotNull(extracted);
        Assert.Equal(".png", extracted.Extension);
        AssertPng(extracted.Data, 4, 3, components, pixels);
    }

    [Fact]
    public void SingleElementFilterAndParameterArrays_ReversePngPredictors()
    {
        byte[] rows = { 0, 10, 20, 30, 40, 50, 60, 2, 1, 2, 3, 4, 5, 6 };
        byte[] pixels = { 10, 20, 30, 40, 50, 60, 11, 22, 33, 44, 55, 66 };
        var document = ImageDocument(2, 2, 3, rows,
            filter: "[/FlateDecode]", extra: "/DecodeParms [<< /Predictor 15 /Colors 3 /Columns 2 /BitsPerComponent 8 >>]");

        var extracted = PdfImageExtractor.TryExtract(document, Assert.Single(PdfImages.Enumerate(document)));

        Assert.NotNull(extracted);
        AssertPng(extracted.Data, 2, 2, 3, pixels);
    }

    [Theory]
    [InlineData("/Filter", "JPXDecode")]
    [InlineData("/ColorSpace", "Indexed")]
    [InlineData("/ColorSpace", "DeviceCMYK")]
    public void UnsupportedNames_ReturnNull(string key, string name)
    {
        var document = ImageDocument(2, 2, 3, new byte[12]);
        var info = Assert.Single(PdfImages.Enumerate(document));
        var stream = Assert.IsType<PdfStream>(document.GetObject(info.Reference.ObjectNumber));
        stream.Dictionary[key[1..]] = new PdfName(name);
        Assert.Null(PdfImageExtractor.TryExtract(document, info));
    }

    [Theory]
    [InlineData("/SMask << /Type /XObject >>")]
    [InlineData("/Mask [0 0 0 0 0 0]")]
    [InlineData("/Decode [1 0 1 0 1 0]")]
    [InlineData("/ImageMask true")]
    [InlineData("/SMaskInData 1")]
    [InlineData("/DecodeParms << /Predictor 2 >>")]
    [InlineData("/DecodeParms << /Predictor 15 /Columns 2 /Colors 1 >>")]
    public void UnsupportedSampleInterpretation_ReturnsNull(string extra)
    {
        var document = ImageDocument(2, 2, 3, new byte[12], extra: extra);
        Assert.Null(PdfImageExtractor.TryExtract(document, Assert.Single(PdfImages.Enumerate(document))));
    }

    [Fact]
    public void FilterChainsAndNonEightBitSamples_AreNotMistakenForSimpleFlate()
    {
        var document = ImageDocument(2, 2, 3, new byte[12], filter: "[/FlateDecode /DCTDecode]");
        Assert.Null(PdfImageExtractor.TryExtract(document, Assert.Single(PdfImages.Enumerate(document))));
        document = ImageDocument(2, 2, 3, new byte[12], bits: 16);
        Assert.Null(PdfImageExtractor.TryExtract(document, Assert.Single(PdfImages.Enumerate(document))));
    }

    [Fact]
    public void InvalidBuffersAndDimensionBombs_AreRejectedBeforeWriting()
    {
        using var output = new MemoryStream();
        Assert.Throws<PdfOperationException>(() => PdfPngWriter.Write(output, 2, 2, 3, new byte[11]));
        Assert.Throws<PdfOperationException>(() => PdfPngWriter.Write(output, 2, 2, 4, new byte[16]));
        Assert.Throws<PdfOperationException>(() => PdfPngWriter.Write(output, 0, 1, 1, Array.Empty<byte>()));
        Assert.Throws<PdfLimitExceededException>(() => PdfPngWriter.Write(output, int.MaxValue, int.MaxValue, 3, Array.Empty<byte>()));
        Assert.Throws<PdfLimitExceededException>(() => PdfPngWriter.Write(output, 10_000, 10_000, 3, Array.Empty<byte>()));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void InflatedSampleCount_IsBoundedByDeclaredImageDimensions()
    {
        var document = ImageDocument(1, 1, 3, new byte[4096]);
        Assert.Throws<PdfLimitExceededException>(() => PdfImageExtractor.TryExtract(document, Assert.Single(PdfImages.Enumerate(document))));
        document = ImageDocument(2, 2, 3, new byte[11]);
        Assert.Throws<PdfFormatException>(() => PdfImageExtractor.TryExtract(document, Assert.Single(PdfImages.Enumerate(document))));
        document = ImageDocument(int.MaxValue, 1, 3, new byte[3]);
        Assert.Throws<PdfLimitExceededException>(() => PdfImageExtractor.TryExtract(document, Assert.Single(PdfImages.Enumerate(document))));
    }

    [Fact]
    public void Cancellation_IsObservedByScanExtractionAndWriter()
    {
        var document = ImageDocument(2, 2, 3, new byte[12]);
        var image = Assert.Single(PdfImages.Enumerate(document));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PdfImages.Enumerate(document, cancellationToken: canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => PdfImageExtractor.TryExtract(document, image, canceled.Token));
        using var output = new MemoryStream();
        Assert.ThrowsAny<OperationCanceledException>(() => PdfPngWriter.Write(output, 2, 2, 3, new byte[12], canceled.Token));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void MutatedImageStreams_OnlyFailWithTypedPdfErrors()
    {
        var random = new Random(111);
        int extracted = 0;
        for (int i = 0; i < 200; i++)
        {
            var document = ImageDocument(4, 4, 3, Enumerable.Range(0, 48).Select(n => (byte)n).ToArray());
            var image = Assert.Single(PdfImages.Enumerate(document));
            var stream = Assert.IsType<PdfStream>(document.GetObject(image.Reference.ObjectNumber));
            if (i % 2 == 0) stream.Data[random.Next(stream.Data.Length)] ^= (byte)(1 << random.Next(8));
            else stream.Dictionary["Width"] = new PdfInteger(random.Next(0, 10));
            try
            {
                var result = PdfImageExtractor.TryExtract(document, image);
                if (result is not null) { Assert.Equal(".png", result.Extension); extracted++; }
            }
            catch (PdfException) { }
        }
        Assert.True(extracted > 0);
    }

    private static PdfDocument ImageDocument(int width, int height, int components, byte[] samples,
        string filter = "/FlateDecode", string extra = "", int bits = 8) =>
        PdfDocument.Load(TestPdfFactory.Serialize(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im0 4 0 R >> >> >>",
            TestPdfFactory.StreamObject($"/Type /XObject /Subtype /Image /Width {width} /Height {height} /BitsPerComponent {bits} /ColorSpace /{(components == 1 ? "DeviceGray" : "DeviceRGB")} /Filter {filter} {extra}",
                Encoding.Latin1.GetString(TestPdfFactory.Zlib(samples)))
        }, "/Root 1 0 R"));

    // Independent PNG inspection: bitwise CRC rather than the writer's lookup table,
    // standard zlib inflation, and direct raw-row comparison rather than a PDF round trip.
    private static void AssertPng(byte[] png, int width, int height, int components, byte[] pixels)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());
        int offset = 8;
        bool headerSeen = false, endSeen = false;
        using var idat = new MemoryStream();
        while (offset < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            Assert.InRange(length, 0, png.Length - offset - 12);
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            uint crc = uint.MaxValue;
            foreach (byte value in png.AsSpan(offset + 4, length + 4))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
            }
            Assert.Equal(~crc, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4)));
            if (type == "IHDR")
            {
                Assert.False(headerSeen);
                Assert.Equal(8, offset);
                Assert.Equal(13, length);
                Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(data));
                Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(data[4..]));
                Assert.Equal(8, data[8]);
                Assert.Equal(components == 1 ? 0 : 2, data[9]);
                Assert.All(data[10..].ToArray(), b => Assert.Equal(0, b));
                headerSeen = true;
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") { Assert.Equal(0, length); endSeen = true; Assert.Equal(png.Length, offset + 12); }
            else Assert.Fail("Unexpected PNG chunk.");
            offset += length + 12;
        }
        Assert.True(headerSeen && endSeen);
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var rows = new MemoryStream();
        zlib.CopyTo(rows);
        byte[] raw = rows.ToArray();
        int stride = width * components;
        Assert.Equal((stride + 1) * height, raw.Length);
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, raw[y * (stride + 1)]);
            Assert.Equal(pixels.Skip(y * stride).Take(stride), raw.Skip(y * (stride + 1) + 1).Take(stride));
        }
    }
}
