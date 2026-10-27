using FileKit.Html;
using FileKit.Html.Testing;
using PowerTrove.Pack.Pdf.Html;
using PowerTrove.Abstractions.Packs;
using Windows.Data.Pdf;
using Windows.Storage;

namespace PowerTrove.Pack.Pdf.Windows.Tests;

public sealed class EpubPdfRendererTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    [Fact]
    public async Task DefaultFactoryConvertsGeneratedBookToTwoStyledPagesWithEmbeddedImage()
    {
        byte[] png = await Png();
        string input = _directory.Write("book.epub", TestEpubFactory.Create(first: "<h1>First chapter</h1><img width='100' height='100' src='../images/blue.png'>", image: png));
        var pack = new PdfPackFactory().Create(new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier())));
        var action = pack.Actions.Single(a => a.Descriptor.Id == "powertrove.pdf.epub-to-pdf");
        var parameters = Snapshots.Parameters(("Paper", "letter"), ("MarginMm", "12")); var context = Snapshots.ForPaths(input);
        Assert.True((await action.MatchAsync(context, default)).IsAvailable); Assert.False((await action.PreviewAsync(context, parameters, default)).IsBlocked);
        var result = await action.ExecuteAsync(context, parameters, null, default); Assert.True(result.IsSuccess, result.Message); Assert.Single(result.OutputPaths);
        var pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(result.OutputPaths[0])); Assert.Equal(2u, pdf.PageCount);
        using var page = pdf.GetPage(1); Assert.InRange(page.Size.Width, 814, 818); Assert.InRange(page.Size.Height, 1054, 1058);
        using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream(); await page.RenderToStreamAsync(stream);
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream); byte[] pixels = (await decoder.GetPixelDataAsync()).DetachPixelData();
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[i * 4 + 2] > 170 && pixels[i * 4] < 100 && pixels[i * 4 + 1] < 100);
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[i * 4] > 170 && pixels[i * 4 + 1] < 100 && pixels[i * 4 + 2] < 100);
    }
    private static async Task<byte[]> Png()
    {
        using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, 2, 2, 96, 96, Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, 4).SelectMany(p => p).ToArray());
        await encoder.FlushAsync(); stream.Seek(0); var bytes = new byte[stream.Size];
        using var reader = new global::Windows.Storage.Streams.DataReader(stream); await reader.LoadAsync((uint)bytes.Length); reader.ReadBytes(bytes); return bytes;
    }
}
