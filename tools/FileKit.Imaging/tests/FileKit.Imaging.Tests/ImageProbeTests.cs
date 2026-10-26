using System.Buffers.Binary;
using System.Text;
using FileKit.Imaging;

namespace FileKit.Imaging.Tests;

public class ImageProbeTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("sample.png", ImageFormat.Png)]
    [InlineData("sample.jpg", ImageFormat.Jpeg)]
    [InlineData("sample.gif", ImageFormat.Gif)]
    [InlineData("sample.bmp", ImageFormat.Bmp)]
    [InlineData("sample.tif", ImageFormat.Tiff)]
    [InlineData("sample-lossy.webp", ImageFormat.WebP)]
    [InlineData("sample-lossless.webp", ImageFormat.WebP)]
    [InlineData("sample.avif", ImageFormat.Avif)]
    public async Task ProbeFile_ReadsDimensionsOfRealEncoderOutput(string file, ImageFormat format)
    {
        var info = await ImageProbe.ProbeFileAsync(Fixture(file));

        Assert.Equal(format, info.Format);
        Assert.Equal(37, info.Width);
        Assert.Equal(21, info.Height);
    }

    [Fact]
    public void Probe_JpegExifOrientation_SwapsDisplaySize()
    {
        byte[] jpeg = File.ReadAllBytes(Fixture("sample.jpg"));
        var tiff = new byte[] { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0, 0, 0, 0, 0 };
        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var segment = new byte[] { 0xFF, 0xE1, 0, (byte)(payload.Length + 2) }.Concat(payload);
        var rotated = jpeg.Take(2).Concat(segment).Concat(jpeg.Skip(2)).ToArray();

        var info = ImageProbe.Probe(rotated);

        Assert.Equal(6, info.Orientation);
        Assert.Equal((21, 37), info.DisplaySize);
    }

    [Fact]
    public void Probe_HugeDeclaredDimensions_AreReportedWithoutDecoding()
    {
        // A "pixel flood" PNG: tiny file, enormous declared size.
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }.CopyTo(png, 0);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(png, 12);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 100_000);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 100_000);

        var info = ImageProbe.Probe(png);

        Assert.Equal(10_000_000_000L, info.PixelCount);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF })]
    [InlineData(new byte[] { 0x42, 0x4D, 0, 0 })]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 })]
    public void Probe_UnknownOrTruncated_Throws(byte[] data)
    {
        Assert.Throws<ImageProbeException>(() => ImageProbe.Probe(data));
    }

    [Fact]
    public void Probe_ZeroDimensions_AreRejected()
    {
        var gif = Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[] { 0, 0, 5, 0 }).ToArray();
        Assert.Throws<ImageProbeException>(() => ImageProbe.Probe(gif));
    }

    [Fact]
    public void Probe_MutatedFixtures_FailOnlyWithProbeException()
    {
        var seeds = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures")).Select(File.ReadAllBytes).ToArray();
        var random = new Random(99);
        for (int i = 0; i < 3000; i++)
        {
            var data = (byte[])seeds[i % seeds.Length].Clone();
            if (i % 3 == 0) Array.Resize(ref data, random.Next(0, data.Length));
            else for (int k = 0; k < 4; k++) if (data.Length > 0) data[random.Next(data.Length)] = (byte)random.Next(256);
            try { ImageProbe.Probe(data); } catch (ImageProbeException) { }
        }
    }
}
