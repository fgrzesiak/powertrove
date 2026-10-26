using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using ContextBar.Pack.Pdf.Windows;
using FileKit.Pdf;
using Xunit;

namespace ContextBar.Platform.Windows.Tests;

public class WicPdfImageRecompressorTests
{
    [Theory]
    [InlineData(2400, 1600)]
    [InlineData(1, 1)]
    public async Task RecompressAsync_WithJpeg_ReturnsReplacement(int declaredWidth, int declaredHeight)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) return;

        var recompressor = new WicPdfImageRecompressor();

        byte[] jpegBytes = await CreateJpegAsync();
        var info = ImageInfo(declaredWidth, declaredHeight, "DeviceRGB", "DCTDecode", jpegBytes.Length);

        var result = await recompressor.RecompressAsync(jpegBytes, "DCTDecode", info, 1200, 800, 75, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1200, result!.Width);
        Assert.Equal(800, result.Height);
        using var output = new InMemoryRandomAccessStream();
        await output.WriteAsync(result.JpegData.AsBuffer());
        output.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(output);
        Assert.Equal(1200u, decoder.PixelWidth);
        Assert.Equal(800u, decoder.PixelHeight);
    }

    [Fact]
    public async Task RecompressAsync_ActualJpegDimensionsExceedBudget_ReturnsNull()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) return;
        byte[] jpeg = await CreateJpegAsync();
        // Change the JPEG frame header without allocating an oversized source raster.
        int frame = -1;
        for (int i = 0; i < jpeg.Length - 8; i++)
        {
            if (jpeg[i] == 0xff && jpeg[i + 1] is 0xc0 or 0xc2)
            {
                frame = i;
                break;
            }
        }
        Assert.True(frame >= 0);
        jpeg[frame + 5] = jpeg[frame + 7] = 0x27;
        jpeg[frame + 6] = jpeg[frame + 8] = 0x10; // 10000 x 10000
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(jpeg.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        Assert.Equal(10000u, decoder.PixelWidth);
        Assert.Equal(10000u, decoder.PixelHeight);

        var result = await new WicPdfImageRecompressor().RecompressAsync(jpeg, "DCTDecode",
            ImageInfo(1, 1, "DeviceRGB", "DCTDecode", jpeg.Length), 1200, 800, 75, CancellationToken.None);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(int.MaxValue, int.MaxValue, "DeviceRGB", 0)]
    [InlineData(65536, 65536, "DeviceRGB", 0)]
    [InlineData(30000, 30000, "DeviceGray", 0)]
    [InlineData(0, 10, "DeviceRGB", 0)]
    [InlineData(-1, 10, "DeviceGray", 0)]
    [InlineData(2400, 1600, "DeviceRGB", 3)]
    [InlineData(2400, 1600, "DeviceGray", 1)]
    [InlineData(1, 1, "DeviceRGB", 4)]
    [InlineData(1, 1, "DeviceGray", 2)]
    public async Task RecompressAsync_InvalidFlateDimensionsOrBuffer_ReturnsNull(int width, int height, string colorSpace, int length)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) return;
        var result = await new WicPdfImageRecompressor().RecompressAsync(new byte[length], "FlateDecode",
            ImageInfo(width, height, colorSpace, "FlateDecode", length), 1200, 800, 75, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task RecompressAsync_Canceled_ThrowsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WicPdfImageRecompressor().RecompressAsync(
            Array.Empty<byte>(), "FlateDecode", ImageInfo(1, 1, "DeviceRGB", "FlateDecode", 0),
            1200, 800, 75, cancellation.Token));
    }

    private static PdfImageInfo ImageInfo(int width, int height, string colorSpace, string filter, int length) =>
        new(new PdfReference(1, 0), width, height, 8, colorSpace, filter, length, false, new[] { 0 });

    private static async Task<byte[]> CreateJpegAsync()
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        var pixels = new byte[2400 * 1600 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i / 4 % 256);
            pixels[i + 3] = 255;
        }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 2400, 1600, 96, 96, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[checked((int)stream.Size)];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}
