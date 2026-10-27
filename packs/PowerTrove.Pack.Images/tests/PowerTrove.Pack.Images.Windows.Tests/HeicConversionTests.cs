using System.Runtime.InteropServices.WindowsRuntime;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Images.Windows;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PowerTrove.Pack.Images.Windows.Tests;

public sealed class HeicConversionTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task InstalledHeifCodec_ConvertsGeneratedHeicThroughRealDesktopFactory()
    {
        // HEIF is an optional Windows extension. The bundled Windows Magick build cannot encode it.
        var codec = BitmapEncoder.GetEncoderInformationEnumerator().FirstOrDefault(info => info.FileExtensions.Contains(".heic"));
        if (codec is null || !new WindowsImagingEngine().CanDecode(FileFormats.Heic)) return;
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        // A listed HEIF encoder without the HEVC extension (e.g. Windows Server) cannot create the fixture.
        try { encoder = await BitmapEncoder.CreateAsync(codec.CodecId, stream); }
        catch (System.Runtime.InteropServices.COMException) { return; }
        var pixels = new byte[32 * 24 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 100; pixels[i + 2] = 200; pixels[i + 3] = 255; }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 32, 24, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var source = stream.AsStreamForRead();
        using var bytes = new MemoryStream();
        await source.CopyToAsync(bytes);
        byte[] original = bytes.ToArray();
        string path = _directory.Write("generated.heic", original);
        var host = new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new FileSignatureVerifier()));
        var action = new ImagesPackFactory().Create(host).Actions.Single(a => a.Descriptor.Id == "powertrove.image.convert-jpeg");
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, default);
        Assert.True(receipt.IsSuccess, receipt.Message);
        using var output = File.OpenRead(Assert.Single(receipt.OutputPaths));
        var image = await BitmapDecoder.CreateAsync(output.AsRandomAccessStream());
        Assert.Equal((32u, 24u), (image.PixelWidth, image.PixelHeight));
        Assert.Equal(BitmapDecoder.JpegDecoderId, image.DecoderInformation.CodecId);
        Assert.Equal(original, File.ReadAllBytes(path));
    }
}
