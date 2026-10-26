using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileKit.Pdf.Tests;

public class PdfImageTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Jpeg_IsEmbeddedWithoutReencoding()
    {
        byte[] jpeg = File.ReadAllBytes(Fixture("photo.jpg"));
        var image = PdfImage.Load(jpeg);
        using var output = new MemoryStream();

        PdfTools.ImagesToPdf(new[] { image }, output);
        var result = PdfDocument.Load(output.ToArray());

        Assert.Equal((48, 32), (image.Width, image.Height));
        Assert.Equal(1, result.PageCount);
        var xobject = FindImage(result, 0);
        Assert.Equal("DCTDecode", xobject.Dictionary.GetName("Filter"));
        Assert.Equal(jpeg, xobject.Data);
    }

    [Fact]
    public void Jpeg_LandscapeImage_GetsLandscapeA4Page()
    {
        var image = PdfImage.Load(File.ReadAllBytes(Fixture("photo.jpg")));
        using var output = new MemoryStream();

        PdfTools.ImagesToPdf(new[] { image }, output, new PdfImagePageLayout(PdfPageSize.A4));
        var (width, height) = PdfTools.GetPageSize(PdfDocument.Load(output.ToArray()), 0);

        Assert.True(width > height);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(6, true)]
    [InlineData(8, true)]
    public void Jpeg_ExifOrientation_IsApplied(int orientation, bool swapsDimensions)
    {
        byte[] jpeg = WithExifOrientation(File.ReadAllBytes(Fixture("photo.jpg")), orientation);
        var image = PdfImage.Load(jpeg);

        Assert.Equal(orientation, image.Orientation);
        Assert.Equal(swapsDimensions ? (32, 48) : (48, 32), image.DisplaySize);

        using var output = new MemoryStream();
        PdfTools.ImagesToPdf(new[] { image }, output, new PdfImagePageLayout(PdfPageSize.FitImage));
        var (width, height) = PdfTools.GetPageSize(PdfDocument.Load(output.ToArray()), 0);
        Assert.Equal(swapsDimensions, height > width);
    }

    [Fact]
    public void GrayJpeg_UsesDeviceGray()
    {
        var image = PdfImage.Load(File.ReadAllBytes(Fixture("gray.jpg")));
        using var output = new MemoryStream();
        PdfTools.ImagesToPdf(new[] { image }, output);

        Assert.Equal("DeviceGray", FindImage(PdfDocument.Load(output.ToArray()), 0).Dictionary.GetName("ColorSpace"));
    }

    [Theory]
    [InlineData(2, 8, false)]
    [InlineData(0, 8, false)]
    [InlineData(3, 8, false)]
    [InlineData(6, 8, true)]
    [InlineData(4, 8, true)]
    [InlineData(6, 16, true)]
    [InlineData(2, 16, false)]
    public void Png_ColorTypes_AreEmbedded(int colorType, int bitDepth, bool expectsSoftMask)
    {
        byte[] png = CreatePng(7, 5, colorType, bitDepth);
        var image = PdfImage.Load(png);
        using var output = new MemoryStream();

        PdfTools.ImagesToPdf(new[] { image }, output);
        var xobject = FindImage(PdfDocument.Load(output.ToArray()), 0);

        Assert.Equal(expectsSoftMask, xobject.Dictionary["SMask"] is not null);
        Assert.Equal(7, ((PdfInteger)xobject.Dictionary["Width"]!).Value);
    }

    [Fact]
    public void Png_PaletteWithTransparency_GetsSoftMask()
    {
        byte[] png = CreatePng(4, 4, 3, 8, paletteTransparency: new byte[] { 0, 255 });
        using var output = new MemoryStream();

        PdfTools.ImagesToPdf(new[] { PdfImage.Load(png) }, output);

        Assert.NotNull(FindImage(PdfDocument.Load(output.ToArray()), 0).Dictionary["SMask"]);
    }

    [Fact]
    public void Png_Interlaced_IsRejectedWithClearMessage()
    {
        byte[] png = CreatePng(4, 4, 2, 8, interlaced: true);
        var ex = Assert.Throws<PdfFormatException>(() => PdfImage.Load(png));
        Assert.Contains("Interlaced", ex.Message);
    }

    [Fact]
    public void Image_PixelLimit_IsEnforced()
    {
        byte[] png = CreatePng(100, 100, 2, 8);
        Assert.Throws<PdfLimitExceededException>(() => PdfImage.Load(png, new PdfImageLimits { MaxPixels = 1000 }));
    }

    [Fact]
    public void UnsupportedOrDamagedImages_FailWithTypedErrors()
    {
        Assert.Throws<PdfFormatException>(() => PdfImage.Load(Encoding.ASCII.GetBytes("GIF89a....")));
        byte[] jpeg = File.ReadAllBytes(Fixture("photo.jpg"));
        var random = new Random(7);
        for (int i = 0; i < 300; i++)
        {
            var mutated = (byte[])jpeg.Clone();
            if (i % 2 == 0) Array.Resize(ref mutated, random.Next(3, jpeg.Length));
            else for (int k = 0; k < 6; k++) mutated[random.Next(3, mutated.Length)] = (byte)random.Next(256);
            try { PdfImage.Load(mutated); } catch (PdfException) { }
        }
    }

    private static PdfStream FindImage(PdfDocument document, int pageIndex)
    {
        var resources = document.ResolveDictionary(document.Pages[pageIndex].Resources)!;
        var xobjects = document.ResolveDictionary(resources["XObject"])!;
        return (PdfStream)document.Resolve(xobjects.Entries.First().Value)!;
    }

    private static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        // Minimal big-endian TIFF with one IFD entry (Orientation, SHORT, 1).
        var tiff = new List<byte> { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, 0, 0, 0, 0 };
        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var segment = new List<byte> { 0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)((payload.Length + 2) & 0xFF) };
        segment.AddRange(payload);
        return jpeg.Take(2).Concat(segment).Concat(jpeg.Skip(2)).ToArray();
    }

    internal static byte[] CreatePng(int width, int height, int colorType, int bitDepth, bool interlaced = false, byte[]? paletteTransparency = null)
    {
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 1 };
        int rowLength = (width * channels * bitDepth + 7) / 8;
        var raw = new MemoryStream();
        var random = new Random(width * 31 + colorType);
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte((byte)(y % 5)); // exercise all filter types
            var row = new byte[rowLength];
            random.NextBytes(row);
            if (colorType == 3) for (int i = 0; i < row.Length; i++) row[i] %= 2;
            raw.Write(row);
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)colorType;
        ihdr[12] = (byte)(interlaced ? 1 : 0);
        WriteChunk(png, "IHDR", ihdr);
        if (colorType == 3)
        {
            WriteChunk(png, "PLTE", new byte[] { 255, 0, 0, 0, 0, 255 });
            if (paletteTransparency is not null) WriteChunk(png, "tRNS", paletteTransparency);
        }
        WriteChunk(png, "IDAT", TestPdfFactory.Zlib(raw.ToArray()));
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(Encoding.ASCII.GetBytes(type));
        stream.Write(data);
        stream.Write(new byte[4]); // CRC is not validated by the reader
    }
}
