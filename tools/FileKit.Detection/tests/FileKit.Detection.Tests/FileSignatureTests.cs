using System.Text;
using FileKit.Detection;

namespace FileKit.Detection.Tests;

public class FileSignatureTests
{
    public static TheoryData<byte[], FileFormat> KnownSignatures => new()
    {
        { new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10 }, FileFormat.Jpeg },
        { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }, FileFormat.Png },
        { Ascii("GIF89a\u0001\u0000"), FileFormat.Gif },
        { Ascii("GIF87a\u0001\u0000"), FileFormat.Gif },
        { new byte[] { 0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0 }, FileFormat.Tiff },
        { new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 8 }, FileFormat.Tiff },
        { Riff("WEBP"), FileFormat.WebP },
        { Riff("WAVE"), FileFormat.Wav },
        { Riff("AVI "), FileFormat.Avi },
        { Ftyp("avif", "mif1", "miaf"), FileFormat.Avif },
        { Ftyp("mif1", "avif"), FileFormat.Avif },
        { Ftyp("heic", "mif1"), FileFormat.Heic },
        { Ftyp("mif1", "heic"), FileFormat.Heic },
        { Ftyp("isom", "iso2", "mp41"), FileFormat.IsoMedia },
        { Ftyp("M4A ", "isom"), FileFormat.IsoMedia },
        { Ftyp("qt  "), FileFormat.IsoMedia },
        { Ascii("fLaC").Concat(new byte[] { 0, 0, 0, 34 }).ToArray(), FileFormat.Flac },
        { Ascii("OggS\u0000\u0002"), FileFormat.Ogg },
        { Ascii("ID3\u0004\u0000\u0000"), FileFormat.Mp3 },
        { new byte[] { 0xFF, 0xFB, 0x90, 0x64 }, FileFormat.Mp3 },
        { new byte[] { 0xFF, 0xF1, 0x50, 0x80 }, FileFormat.Aac },
        { new byte[] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9 }, FileFormat.Asf },
        { new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }, FileFormat.Zip },
        { new byte[] { 0x50, 0x4B, 0x05, 0x06, 0x00, 0x00 }, FileFormat.Zip },
        { new byte[] { 0x1F, 0x8B, 0x08, 0x00 }, FileFormat.Gzip },
        { new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 }, FileFormat.SevenZip },
        { Ascii("Rar!\u001A\u0007\u0001\u0000"), FileFormat.Rar },
        { Ascii("MZ\u0090\u0000"), FileFormat.WindowsExecutable },
        { Ascii("%PDF-1.7\n%"), FileFormat.Pdf },
        { new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x10, 0x10 }, FileFormat.Ico },
    };

    [Theory]
    [MemberData(nameof(KnownSignatures))]
    public void Detect_RecognizesSignature(byte[] header, FileFormat expected)
    {
        Assert.Equal(expected, FileSignature.Detect(header).Format);
    }

    [Fact]
    public void Detect_Bmp_RequiresPlausibleDibHeader()
    {
        var bmp = new byte[54];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        bmp[14] = 40;
        Assert.Equal(FileFormat.Bmp, FileSignature.Detect(bmp).Format);

        bmp[14] = 99;
        Assert.Equal(FileFormat.Unknown, FileSignature.Detect(bmp).Format);
    }

    [Fact]
    public void Detect_Pdf_AllowsLeadingGarbageWithinHeaderWindow()
    {
        var data = new byte[600];
        Encoding.ASCII.GetBytes("%PDF-1.4").CopyTo(data, 500);
        Assert.Equal(FileFormat.Pdf, FileSignature.Detect(data).Format);
    }

    [Fact]
    public void Detect_Matroska_ReportsWebMDocType()
    {
        var header = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x82, 0x84 }.Concat(Ascii("webm")).ToArray();
        var result = FileSignature.Detect(header);
        Assert.Equal(FileFormat.Matroska, result.Format);
        Assert.Equal("webm", result.Subtype);
    }

    [Fact]
    public void Detect_IsoMedia_ReportsMajorBrand()
    {
        var result = FileSignature.Detect(Ftyp("M4A ", "isom"));
        Assert.Equal("M4A ", result.Subtype);
    }

    [Fact]
    public void Detect_FtypWithIcoLikeBoxSize_IsIsoMedia()
    {
        var header = Ftyp("isom");
        header[0] = 0; header[1] = 0; header[2] = 1; header[3] = 0;
        Assert.Equal(FileFormat.IsoMedia, FileSignature.Detect(header).Format);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46 })]
    [InlineData(new byte[] { 0x23, 0x21, 0x2F, 0x62, 0x69, 0x6E })]
    public void Detect_UnknownOrTruncated_ReturnsUnknown(byte[] header)
    {
        Assert.Equal(FileFormat.Unknown, FileSignature.Detect(header).Format);
    }

    [Fact]
    public void Detect_RandomInput_NeverThrows()
    {
        var random = new Random(1234);
        for (int i = 0; i < 20_000; i++)
        {
            var data = new byte[random.Next(0, 80)];
            random.NextBytes(data);
            _ = FileSignature.Detect(data);
        }
    }

    [Fact]
    public async Task DetectAsync_ReadsOnlyHeaderWindow()
    {
        var data = new byte[1_000_000];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        using var stream = new MemoryStream(data);

        var result = await FileSignature.DetectAsync(stream);

        Assert.Equal(FileFormat.Png, result.Format);
        Assert.Equal(FileSignature.HeaderLength, stream.Position);
    }

    [Fact]
    public async Task DetectFileAsync_UsesContentNotExtension()
    {
        string path = Path.Combine(Path.GetTempPath(), $"filekit-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, Ascii("%PDF-1.7\n"));
        try
        {
            Assert.Equal(FileFormat.Pdf, (await FileSignature.DetectFileAsync(path)).Format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Ascii(string value) => Encoding.Latin1.GetBytes(value);

    private static byte[] Riff(string form) =>
        Ascii("RIFF").Concat(new byte[] { 0x24, 0, 0, 0 }).Concat(Ascii(form)).Concat(new byte[8]).ToArray();

    private static byte[] Ftyp(string major, params string[] compatible)
    {
        int size = 16 + 4 * compatible.Length;
        var bytes = new List<byte> { 0, 0, 0, (byte)size };
        bytes.AddRange(Ascii("ftyp"));
        bytes.AddRange(Ascii(major));
        bytes.AddRange(new byte[] { 0, 0, 0, 0 });
        foreach (var brand in compatible) bytes.AddRange(Ascii(brand));
        bytes.AddRange(new byte[8]);
        return bytes.ToArray();
    }
}
