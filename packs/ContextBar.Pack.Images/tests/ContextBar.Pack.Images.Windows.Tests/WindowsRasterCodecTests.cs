using System.Runtime.InteropServices.WindowsRuntime;
using ContextBar.Pack.Images;
using ContextBar.Pack.Images.Windows;
using FileKit.Imaging;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ContextBar.Platform.Windows.Tests;

public class WindowsRasterCodecTests
{
    [Fact]
    public async Task Png_RoundTripsStraightAlphaAndDpi()
    {
        if (!OperatingSystem.IsWindows()) return;
        var codec = new WindowsRasterCodec(); var pixels = new byte[] { 10, 20, 30, 128, 50, 60, 70, 255 };
        using var output = new MemoryStream(); await codec.EncodeAsync(new ImageRaster(2, 1, pixels, 144, 144), output, FileFormats.Png, 90, CancellationToken.None);
        output.Position = 0; var decoded = await codec.DecodeAsync(output, FileFormats.Png, 2, CancellationToken.None);
        Assert.Equal((2, 1), (decoded.Width, decoded.Height)); Assert.Equal(pixels, decoded.Bgra32); Assert.InRange(decoded.DpiX, 143, 145);
    }
    [Fact]
    public async Task Jpeg_FlattensTransparentPixelsOntoWhite()
    {
        if (!OperatingSystem.IsWindows()) return;
        var codec = new WindowsRasterCodec(); var pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < pixels.Length; i += 4) pixels[i + 2] = 255;
        using var output = new MemoryStream(); await codec.EncodeAsync(new ImageRaster(8, 8, pixels), output, FileFormats.Jpeg, 95, CancellationToken.None);
        output.Position = 0; var decoded = await codec.DecodeAsync(output, FileFormats.Jpeg, 64, CancellationToken.None);
        Assert.True(decoded.Bgra32[0] > 245 && decoded.Bgra32[1] > 245 && decoded.Bgra32[2] > 245);
    }
    [Fact]
    public async Task Jpeg_GrayInputStaysGray()
    {
        // The WinRT JPEG encoder accepts no Gray8 input, so gray images are written as neutral BGR.
        if (!OperatingSystem.IsWindows()) return;
        var gray = RasterOperations.Canvas(8, 8, new BgraColor(80, 80, 80)); using var encoded = new MemoryStream();
        var codec = new WindowsRasterCodec();
        await codec.EncodeAsync(new ImageRaster(8, 8, gray.Bgra), encoded, FileFormats.Jpeg, 95, CancellationToken.None);
        encoded.Position = 0;
        var decoded = await codec.DecodeAsync(encoded, FileFormats.Jpeg, 64, CancellationToken.None);
        for (int i = 0; i < decoded.Bgra32.Length; i += 4)
        {
            Assert.InRange(Math.Abs(decoded.Bgra32[i] - decoded.Bgra32[i + 1]), 0, 2);
            Assert.InRange(Math.Abs(decoded.Bgra32[i] - decoded.Bgra32[i + 2]), 0, 2);
        }
    }
    [Fact]
    public async Task Decode_RespectsExifOrientationAndChecksPixelsBeforeAllocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var input = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, input);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 16, 8, 96, 96, RasterOperations.Canvas(16, 8, new BgraColor(10, 30, 50)).Bgra);
        var metadata = new BitmapPropertySet { { "System.Photo.Orientation", new BitmapTypedValue((ushort)6, global::Windows.Foundation.PropertyType.UInt16) } };
        await encoder.BitmapProperties.SetPropertiesAsync(metadata); await encoder.FlushAsync(); input.Seek(0);
        using var source = input.AsStreamForRead(); var codec = new WindowsRasterCodec();
        var raster = await codec.DecodeAsync(source, FileFormats.Jpeg, 128, CancellationToken.None); Assert.Equal((8, 16), (raster.Width, raster.Height));
        source.Position = 0;
        await Assert.ThrowsAsync<FileProcessingException>(() => codec.DecodeAsync(source, FileFormats.Jpeg, 127, CancellationToken.None));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolPng_RgbAndRgbaDecodeWithWic(bool alpha)
    {
        if (!OperatingSystem.IsWindows()) return;
        var raster = RasterOperations.Canvas(5, 3, new BgraColor(23, 81, 179, alpha ? (byte)128 : (byte)255));
        using var png = new MemoryStream(); PngEncoder.Encode(raster, png, alpha); png.Position = 0;
        var decoded = await new WindowsRasterCodec().DecodeAsync(png, FileFormats.Png, 15, CancellationToken.None); Assert.Equal(raster.Bgra, decoded.Bgra32);
    }
    [Fact]
    public async Task ToolIndexedPngAndIcon_AreReadableWithWic()
    {
        if (!OperatingSystem.IsWindows()) return;
        var raster = RasterOperations.Canvas(16, 16, new BgraColor(23, 81, 179, 128));
        using var png = new MemoryStream(); PngEncoder.Encode(PaletteQuantizer.Quantize(raster, 16, false), png); png.Position = 0;
        var codec = new WindowsRasterCodec(); var decoded = await codec.DecodeAsync(png, FileFormats.Png, 256, CancellationToken.None); Assert.Equal(raster.Bgra, decoded.Bgra32);
        using var icon = new MemoryStream(); IcoWriter.WriteImages(icon, new[] { raster }); icon.Position = 0;
        var iconRaster = await codec.DecodeAsync(icon, FileFormats.Ico, 256, CancellationToken.None); Assert.Equal((16, 16), (iconRaster.Width, iconRaster.Height)); Assert.Equal(raster.Bgra, iconRaster.Bgra32);
    }
}
