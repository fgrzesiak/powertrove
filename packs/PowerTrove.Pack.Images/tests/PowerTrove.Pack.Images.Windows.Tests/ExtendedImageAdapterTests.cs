using System.Runtime.InteropServices.WindowsRuntime;
using PowerTrove.Pack.Images;
using PowerTrove.Pack.Images.Windows;
using PowerTrove.Platform.Windows.Imaging;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PowerTrove.Platform.Windows.Tests;

public class ExtendedImageAdapterTests
{
    [Fact]
    public async Task Gif_WicReadsFrameCountTimingLoopAndCanvas()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var output = new MemoryStream();
        await new WindowsAnimatedGifEncoder().EncodeAsync(Frames(), output, new GifEncodingOptions(8, 4, 7, true), CancellationToken.None);
        using var stream = new InMemoryRandomAccessStream(); await stream.WriteAsync(output.ToArray().AsBuffer()); stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream); Assert.Equal(2u, decoder.FrameCount);
        var container = await decoder.BitmapContainerProperties.GetPropertiesAsync(new[] { "/logscrdesc/Width", "/logscrdesc/Height", "/appext/Application", "/appext/Data" });
        Assert.Equal((ushort)8, container["/logscrdesc/Width"].Value); Assert.Equal((ushort)4, container["/logscrdesc/Height"].Value);
        Assert.Equal("NETSCAPE2.0"u8.ToArray(), (byte[])container["/appext/Application"].Value);
        // NETSCAPE2.0 loop sub-block: size 3, id 1, loop count 0 (infinite). WIC returns it without
        // the block terminator it writes itself, so only the sub-block is compared.
        Assert.Equal(new byte[] { 3, 1, 0, 0 }, ((byte[])container["/appext/Data"].Value).Take(4).ToArray());
        for (uint i = 0; i < 2; i++)
        {
            var frame = await decoder.GetFrameAsync(i); var properties = await frame.BitmapProperties.GetPropertiesAsync(new[] { "/grctlext/Delay", "/grctlext/Disposal" });
            Assert.Equal((ushort)7, properties["/grctlext/Delay"].Value); Assert.Equal((byte)2, properties["/grctlext/Disposal"].Value);
            Assert.Equal((8u, 4u), (frame.PixelWidth, frame.PixelHeight));
        }
        static async IAsyncEnumerable<ImageRaster> Frames()
        {
            yield return new ImageRaster(8, 4, RasterOperations.Canvas(8, 4, new BgraColor(255, 0, 0)).Bgra);
            await Task.Yield(); yield return new ImageRaster(8, 4, RasterOperations.Canvas(8, 4, new BgraColor(0, 0, 255)).Bgra);
        }
    }
    [Fact]
    public async Task FaceDetection_GeneratedBlankImageHasNoFaces()
    {
        if (!OperatingSystem.IsWindows()) return;
        var detector = new WindowsFaceDetector(); if (!detector.IsAvailable) return;
        var faces = await detector.DetectAsync(new ImageRaster(128, 128, RasterOperations.Canvas(128, 128, new BgraColor(255, 255, 255)).Bgra), CancellationToken.None);
        Assert.Empty(faces);
    }
    [Fact]
    public async Task Ocr_RecognizesGeneratedEnglishTextWhenLanguageIsInstalled()
    {
        if (!OperatingSystem.IsWindows()) return;
        var recognizer = new WindowsTextRecognizer(); var language = recognizer.AvailableLanguages.FirstOrDefault(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        if (language is null) return;
        var raster = TextFixture(); using var png = new MemoryStream(); PngEncoder.Encode(raster, png); png.Position = 0;
        var decoded = await new WindowsRasterCodec().DecodeAsync(png, FileFormats.Png, RasterImage.MaxPixels, CancellationToken.None);
        var lines = await recognizer.RecognizeAsync(decoded.Width, decoded.Height, decoded.Bgra32, language, CancellationToken.None);
        Assert.Contains("HELLO", string.Concat(lines.Select(l => l.Text)).Replace(" ", "").ToUpperInvariant());
        Assert.All(lines.SelectMany(l => l.Words), word => { Assert.InRange(word.X, 0, decoded.Width); Assert.InRange(word.Y, 0, decoded.Height); });
    }
    private static RasterImage TextFixture()
    {
        string[][] glyphs = {
            new[] { "10001", "10001", "10001", "11111", "10001", "10001", "10001" },
            new[] { "11111", "10000", "10000", "11110", "10000", "10000", "11111" },
            new[] { "10000", "10000", "10000", "10000", "10000", "10000", "11111" },
            new[] { "10000", "10000", "10000", "10000", "10000", "10000", "11111" },
            new[] { "01110", "10001", "10001", "10001", "10001", "10001", "01110" }
        };
        var raster = RasterOperations.Canvas(408, 132, new BgraColor(255, 255, 255)); const int scale = 12;
        for (int g = 0; g < glyphs.Length; g++) for (int y = 0; y < 7; y++) for (int x = 0; x < 5; x++)
            if (glyphs[g][y][x] == '1') for (int dy = 0; dy < scale; dy++) for (int dx = 0; dx < scale; dx++)
            {
                int p = ((24 + y * scale + dy) * raster.Width + 24 + g * 6 * scale + x * scale + dx) * 4;
                raster.Bgra[p] = raster.Bgra[p + 1] = raster.Bgra[p + 2] = 0;
            }
        return raster;
    }
}
