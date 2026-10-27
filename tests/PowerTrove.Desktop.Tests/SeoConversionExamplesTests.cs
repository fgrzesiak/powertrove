using FileKit.Pdf;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Media;
using PowerTrove.Pack.Media.Windows;
using PowerTrove.Pack.Pdf;
using PowerTrove.Pack.Pdf.Windows;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace PowerTrove.Desktop.Tests;

public sealed class SeoConversionExamplesTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    private static PackHostServices Host() => new(new Version(0, 1, 0),
        FileActionServices.CreatePhysical(new FileSignatureVerifier()));

    [Fact]
    public async Task PngToPdfAndPdfToPng_UseRealFactoriesAndPreserveOriginals()
    {
        var bytes = new byte[320 * 180 * 3];
        for (int y = 0; y < 180; y++)
        for (int x = 0; x < 320; x++)
        {
            int offset = (y * 320 + x) * 3;
            bytes[offset] = (byte)(30 + x * 150 / 320);
            bytes[offset + 1] = (byte)(60 + y * 130 / 180);
            bytes[offset + 2] = (byte)(x > 160 && y > 45 && y < 135 ? 60 : 210);
        }
        string input = _directory.File("generated-image.png");
        using (var stream = File.Create(input)) PdfPngWriter.Write(stream, 320, 180, 3, bytes);
        byte[] original = File.ReadAllBytes(input);
        var pack = new PdfPackFactory().Create(Host());
        var create = pack.Actions.Single(a => a.Descriptor.Id == "powertrove.pdf.images-to-pdf");
        var context = Snapshots.ForPaths(input);
        var options = Snapshots.Parameters(("PageSize", "fit"));
        Assert.False((await create.PreviewAsync(context, options, default)).IsBlocked);
        var result = await create.ExecuteAsync(context, options, null, default);
        Assert.True(result.IsSuccess, result.Message);
        string pdf = Assert.Single(result.OutputPaths);
        Assert.NotEqual(input, pdf);
        Assert.Equal(1, await new WindowsPdfRenderer().GetPageCountAsync(pdf, default));
        byte[] originalPdf = File.ReadAllBytes(pdf);

        var render = pack.Actions.Single(a => a.Descriptor.Id == "powertrove.pdf.to-images");
        var renderContext = Snapshots.ForPaths(pdf);
        var renderOptions = Snapshots.Parameters(("Format", "png"), ("Dpi", "150"));
        Assert.False((await render.PreviewAsync(renderContext, renderOptions, default)).IsBlocked);
        var rendered = await render.ExecuteAsync(renderContext, renderOptions, null, default);
        Assert.True(rendered.IsSuccess, rendered.Message);
        string image = Assert.Single(Directory.GetFiles(Assert.Single(rendered.OutputPaths), "*.png"));
        using (var stream = await (await StorageFile.GetFileFromPathAsync(image)).OpenReadAsync())
        {
            var decoded = await BitmapDecoder.CreateAsync(stream);
            Assert.Equal(BitmapDecoder.PngDecoderId, decoded.DecoderInformation.CodecId);
            Assert.True(decoded.PixelWidth > 0 && decoded.PixelHeight > 0);
            Assert.InRange((double)decoded.PixelWidth / decoded.PixelHeight, 1.76, 1.80);
        }
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal(originalPdf, File.ReadAllBytes(pdf));
        SaveExample("sample-image.png", input);
        SaveExample("sample-image.pdf", pdf);
        SaveExample("sample-pdf-page.png", image);
    }

    [Fact]
    public async Task WavToMp3_UsesTheRealFactoryAndRetainsDurationAndSource()
    {
        string input = MediaFixtures.WriteSineWav(_directory.File("generated-tone.wav"), 2);
        byte[] original = File.ReadAllBytes(input);
        var action = new MediaPackFactory().Create(Host()).Actions.Single(a => a.Descriptor.Id == "powertrove.audio.convert-mp3");
        var context = Snapshots.ForPaths(input);
        Assert.True((await action.MatchAsync(context, default)).IsAvailable);
        Assert.False((await action.PreviewAsync(context, new EmptyParameters(), default)).IsBlocked);
        var result = await action.ExecuteAsync(context, new EmptyParameters(), null, default);
        Assert.True(result.IsSuccess, result.Message);
        string mp3 = Assert.Single(result.OutputPaths);
        Assert.Equal(".mp3", Path.GetExtension(mp3));
        var info = await new WindowsMediaInspector().InspectAsync(mp3, default);
        Assert.True(info.HasAudio);
        Assert.False(info.HasVideo);
        Assert.InRange(info.Duration.TotalSeconds, 1.8, 2.3);
        Assert.Equal(original, File.ReadAllBytes(input));
        SaveExample("sample-tone.wav", input);
        SaveExample("sample-tone.mp3", mp3);
    }

    // Optional export of generated, verified documentation examples. Tests never
    // depend on these exported files or on committed binary fixtures.
    private static void SaveExample(string name, string source)
    {
        string? directory = Environment.GetEnvironmentVariable("POWERTROVE_SEO_EXAMPLE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.Copy(source, Path.Combine(directory, name), overwrite: true);
    }
}
