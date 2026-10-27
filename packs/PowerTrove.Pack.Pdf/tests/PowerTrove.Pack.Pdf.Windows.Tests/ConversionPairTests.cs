using System.Runtime.InteropServices.WindowsRuntime;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PowerTrove.Pack.Pdf.Windows.Tests;

public sealed class ConversionPairTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    [Fact]
    public async Task GeneratedJpegToPdfAndPdfToJpegUseActualPackAndNativeRenderer()
    {
        using var buffer = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, buffer);
        var pixels = new byte[32 * 24 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 120; pixels[i + 1] = 80; pixels[i + 2] = 200; pixels[i + 3] = 255; }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 32, 24, 96, 96, pixels);
        await encoder.FlushAsync(); buffer.Seek(0);
        using var generated = buffer.AsStreamForRead(); using var memory = new MemoryStream(); await generated.CopyToAsync(memory);
        byte[] original = memory.ToArray(); string jpg = _directory.Write("generated.jpg", original);
        var host = new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new FileSignatureVerifier()));
        var pack = new PdfPackFactory().Create(host);
        var toPdf = pack.Actions.Single(a => a.Descriptor.Id == "powertrove.pdf.images-to-pdf");
        var pdfReceipt = await toPdf.ExecuteAsync(Snapshots.ForPaths(jpg), new EmptyParameters(), null, default);
        Assert.True(pdfReceipt.IsSuccess, pdfReceipt.Message);
        string pdf = Assert.Single(pdfReceipt.OutputPaths); byte[] pdfOriginal = File.ReadAllBytes(pdf);
        var toJpeg = pack.Actions.Single(a => a.Descriptor.Id == "powertrove.pdf.to-images");
        var context = Snapshots.ForPaths(pdf); var options = Snapshots.Parameters(("Format", "jpeg"), ("Dpi", "72"), ("Pages", "1"));
        Assert.False((await toJpeg.PreviewAsync(context, options, default)).IsBlocked);
        var jpegReceipt = await toJpeg.ExecuteAsync(context, options, null, default);
        Assert.True(jpegReceipt.IsSuccess, jpegReceipt.Message);
        string image = Assert.Single(Directory.GetFiles(Assert.Single(jpegReceipt.OutputPaths), "*.jpg"));
        using var stream = File.OpenRead(image); var decoded = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        Assert.Equal(BitmapDecoder.JpegDecoderId, decoded.DecoderInformation.CodecId);
        Assert.True(decoded.PixelWidth > 0 && decoded.PixelHeight > 0);
        Assert.Equal(original, File.ReadAllBytes(jpg)); Assert.Equal(pdfOriginal, File.ReadAllBytes(pdf));
    }
}
