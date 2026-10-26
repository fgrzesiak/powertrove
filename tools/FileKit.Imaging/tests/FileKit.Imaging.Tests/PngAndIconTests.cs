using System.Buffers.Binary;
using System.IO.Compression;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;

namespace FileKit.Imaging.Tests;

public class PngAndIconTests
{
    [Fact]
    public void Quantizer_KeepsExactColorsAndIsDeterministic()
    {
        var source = new RasterImage(3, 1, new byte[] { 1, 2, 3, 255, 10, 20, 30, 128, 1, 2, 3, 255 });
        foreach (bool dither in new[] { false, true })
        {
            var result = PaletteQuantizer.Quantize(source, 16, dither);
            Assert.Equal(2, result.Palette.Count);
            Assert.Equal(source.Bgra, Expand(result));
            var again = PaletteQuantizer.Quantize(source, 16, dither);
            Assert.Equal(result.Palette, again.Palette); Assert.Equal(result.Indices, again.Indices);
        }
    }
    [Fact]
    public void Quantizer_BoundsPaletteAndDoesNotDiscardAlpha()
    {
        var random = new Random(47); var pixels = new byte[32 * 32 * 4]; random.NextBytes(pixels);
        var source = new RasterImage(32, 32, pixels);
        var result = PaletteQuantizer.Quantize(source, 16, true);
        Assert.InRange(result.Palette.Count, 1, 16);
        Assert.All(result.Indices, index => Assert.InRange((int)index, 0, result.Palette.Count - 1));
        Assert.Contains(result.Palette, c => c.A < 200);
        Assert.Equal(result.Indices, PaletteQuantizer.Quantize(source, 16, true).Indices);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RgbAndRgba_RoundTripThroughIndependentFilterDecoder(bool alpha)
    {
        var random = new Random(21); var pixels = new byte[17 * 9 * 4]; random.NextBytes(pixels);
        if (!alpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var source = new RasterImage(17, 9, pixels); using var png = new MemoryStream();
        PngEncoder.Encode(source, png, includeAlpha: alpha);
        Assert.Equal(source.Bgra, DecodePng(png.ToArray()).Bgra);
    }
    [Fact]
    public void IndexedPng_RoundTripsPaletteAlphaAndShrinksScreenshotPattern()
    {
        var pixels = new byte[128 * 64 * 4];
        var random = new Random(81);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 128; x++)
        {
            int i = (y * 128 + x) * 4, color = random.Next(16); pixels[i] = (byte)(color * 16); pixels[i + 1] = (byte)(255 - color * 16); pixels[i + 2] = 70; pixels[i + 3] = color % 2 == 0 ? (byte)0 : (byte)255;
        }
        var source = new RasterImage(128, 64, pixels); using var indexed = new MemoryStream(); using var rgba = new MemoryStream();
        var quantized = PaletteQuantizer.Quantize(source, 256, false); PngEncoder.Encode(quantized, indexed); PngEncoder.Encode(source, rgba);
        Assert.Equal(source.Bgra, DecodePng(indexed.ToArray()).Bgra);
        Assert.True(indexed.Length < rgba.Length);
    }
    [Fact]
    public void IconDirectory_ContainsCorrectSizesLengthsAndOffsets()
    {
        var entries = new[] { 16, 32, 256 }.Select(size =>
        {
            using var png = new MemoryStream(); PngEncoder.Encode(RasterOperations.Canvas(size, size, new BgraColor(30, 40, 50, 128)), png);
            return new IcoEntry(size, png.ToArray());
        }).ToArray();
        using var ico = new MemoryStream(); IcoWriter.Write(ico, entries); var bytes = ico.ToArray();
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2))); Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        int offset = 6 + 16 * entries.Length;
        for (int i = 0; i < entries.Length; i++)
        {
            int p = 6 + i * 16; Assert.Equal((byte)(entries[i].Size == 256 ? 0 : entries[i].Size), bytes[p]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 8)); Assert.Equal(entries[i].Png.Length, length);
            Assert.Equal(offset, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 12)));
            Assert.Equal(entries[i].Png, bytes.AsSpan(offset, length).ToArray()); offset += length;
        }
        Assert.Equal(bytes.Length, offset);
    }
    [Fact]
    public void PaletteAndIconMutationInputs_AreRejected()
    {
        var image = new RasterImage(1, 1, new byte[4]);
        Assert.Throws<RasterException>(() => PaletteQuantizer.Quantize(image, 257));
        Assert.Throws<RasterException>(() => PngEncoder.Encode(new IndexedRaster(int.MaxValue, 2, new[] { default(BgraColor) }, new byte[1]), Stream.Null));
        Assert.Throws<RasterException>(() => PngEncoder.Encode(new IndexedRaster(1, 1, new[] { default(BgraColor) }, new byte[] { 1 }), Stream.Null));
        Assert.Throws<ImageProbeException>(() => IcoWriter.Write(Stream.Null, new[] { new IcoEntry(256, new byte[2]) }));
        Assert.Throws<ImageProbeException>(() => IcoWriter.Write(Stream.Null, Array.Empty<IcoEntry>()));
    }
    private static byte[] Expand(IndexedRaster raster) => raster.Indices.SelectMany(index =>
    {
        var c = raster.Palette[index]; return new[] { c.B, c.G, c.R, c.A };
    }).ToArray();

    // Test-only decoder intentionally uses inverse filters and independent RGB/BGRA conversion.
    public static RasterImage DecodePng(byte[] data)
    {
        int width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)), height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20));
        byte type = data[25]; int stride = width * (type == 6 ? 4 : type == 2 ? 3 : 1), bpp = stride / width;
        byte[] palette = Array.Empty<byte>(), alpha = Array.Empty<byte>(); using var packed = new MemoryStream();
        for (int p = 8; p < data.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p)); var chunk = data.AsSpan(p + 4, 4); var payload = data.AsSpan(p + 8, length);
            if (chunk.SequenceEqual("IDAT"u8)) packed.Write(payload);
            if (chunk.SequenceEqual("PLTE"u8)) palette = payload.ToArray();
            if (chunk.SequenceEqual("tRNS"u8)) alpha = payload.ToArray(); p += length + 12;
        }
        packed.Position = 0; using var zlib = new ZLibStream(packed, CompressionMode.Decompress); var previous = new byte[stride]; var output = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int filter = zlib.ReadByte(); var row = new byte[stride]; zlib.ReadExactly(row);
            for (int i = 0; i < stride; i++)
            {
                int left = i >= bpp ? row[i - bpp] : 0, up = previous[i], corner = i >= bpp ? previous[i - bpp] : 0;
                int predicted = 0;
                if (filter == 1) predicted = left; if (filter == 2) predicted = up; if (filter == 3) predicted = (left + up) / 2;
                if (filter == 4)
                {
                    int guess = left + up - corner; var choices = new[] { left, up, corner };
                    predicted = choices.OrderBy(v => Math.Abs(v - guess)).First();
                }
                row[i] = unchecked((byte)(row[i] + predicted));
            }
            for (int x = 0; x < width; x++)
            {
                int d = (y * width + x) * 4, s = x * bpp;
                output[d] = type == 3 ? palette[row[x] * 3 + 2] : row[s + 2];
                output[d + 1] = type == 3 ? palette[row[x] * 3 + 1] : row[s + 1];
                output[d + 2] = type == 3 ? palette[row[x] * 3] : row[s];
                output[d + 3] = type == 6 ? row[s + 3] : type == 3 && row[x] < alpha.Length ? alpha[row[x]] : (byte)255;
            }
            previous = row;
        }
        Assert.Equal(-1, zlib.ReadByte()); return new(width, height, output);
    }
}
