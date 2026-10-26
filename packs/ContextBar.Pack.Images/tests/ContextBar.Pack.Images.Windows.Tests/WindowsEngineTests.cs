using ContextBar.Pack.Images;
using ContextBar.Pack.Images.Windows;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ContextBar.Platform.Windows.Tests;

/// <summary>
/// Integration tests against the real Windows APIs (WIC, Windows.Data.Pdf, Media Foundation).
/// They run on Windows only and are skipped elsewhere.
/// </summary>
public sealed class WindowsEngineTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static async Task<byte[]> EncodeAsync(Guid encoderId, int width, int height, byte alpha = 255)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200; pixels[i + 1] = 80; pixels[i + 2] = 40; pixels[i + 3] = alpha;
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    private static async Task<BitmapDecoder> DecodeAsync(byte[] data)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(data.AsBuffer());
        stream.Seek(0);
        return await BitmapDecoder.CreateAsync(stream);
    }

    [Fact]
    public async Task Wic_ConvertsAndResizes()
    {
        if (!OperatingSystem.IsWindows()) return;
        byte[] png = await EncodeAsync(BitmapEncoder.PngEncoderId, 200, 100);
        var destination = new MemoryStream();

        await new WindowsImagingEngine().TransformAsync(new MemoryStream(png), destination,
            new ImageTransformRequest(FileFormats.Png, FileFormats.Jpeg, 85, 50, 50, false, true), CancellationToken.None);

        var decoder = await DecodeAsync(destination.ToArray());
        Assert.Equal(BitmapDecoder.JpegDecoderId, decoder.DecoderInformation.CodecId);
        Assert.Equal((50u, 25u), (decoder.PixelWidth, decoder.PixelHeight));
    }

    [Fact]
    public async Task Wic_TransparentToJpeg_BecomesWhite()
    {
        if (!OperatingSystem.IsWindows()) return;
        byte[] png = await EncodeAsync(BitmapEncoder.PngEncoderId, 8, 8, alpha: 0);
        var destination = new MemoryStream();

        await new WindowsImagingEngine().TransformAsync(new MemoryStream(png), destination,
            new ImageTransformRequest(FileFormats.Png, FileFormats.Jpeg, 90, null, null, false, true), CancellationToken.None);

        var decoder = await DecodeAsync(destination.ToArray());
        var data = (await decoder.GetPixelDataAsync()).DetachPixelData();
        Assert.True(data[0] > 240 && data[1] > 240 && data[2] > 240);
    }

    [Fact]
    public void Wic_RefusesToPreserveMetadata()
    {
        var engine = new WindowsImagingEngine();
        Assert.False(engine.CanPreserveMetadata);
        Assert.True(engine.CanDecode(FileFormats.Jpeg));
        Assert.False(engine.CanEncode(FileFormats.WebP));
    }
}
