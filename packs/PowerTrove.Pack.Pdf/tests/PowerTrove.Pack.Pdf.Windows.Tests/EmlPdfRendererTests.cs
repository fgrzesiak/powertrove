using FileKit.Html.Testing;
using PowerTrove.Abstractions.Packs;
using Windows.Data.Pdf;
using Windows.Storage;

namespace PowerTrove.Pack.Pdf.Windows.Tests;

public sealed class EmlPdfRendererTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultFactoryPrintsActualPlainAndHtmlMailAndExportsRequestedAttachments(bool html)
    {
        byte[] source = html ? TestEmlFactory.Html(await Png()) : TestEmlFactory.Plain(); string input = _directory.Write("mail.eml", source);
        var pack = new PdfPackFactory().Create(new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier())));
        var action = pack.Actions.Single(a => a.Descriptor.Id == "powertrove.pdf.eml-to-pdf");
        var options = Snapshots.Parameters(("Paper", "letter"), ("ExportAttachments", html ? "true" : "false"));
        Assert.True((await action.MatchAsync(Snapshots.ForPaths(input), default)).IsAvailable);
        var result = await action.ExecuteAsync(Snapshots.ForPaths(input), options, null, default); Assert.True(result.IsSuccess, result.Message); Assert.Single(result.OutputPaths);
        string output = html ? Path.Combine(result.OutputPaths[0], "message.pdf") : result.OutputPaths[0];
        var pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(output)); Assert.Equal(1u, pdf.PageCount);
        using var page = pdf.GetPage(0); Assert.InRange(page.Size.Width, 814, 818);
        if (html)
        {
            Assert.Equal(2, Directory.GetFiles(Path.Combine(result.OutputPaths[0], "attachments")).Length);
            using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream(); await page.RenderToStreamAsync(stream);
            var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream); byte[] pixels = (await decoder.GetPixelDataAsync()).DetachPixelData();
            Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[i * 4 + 2] > 170 && pixels[i * 4] < 100 && pixels[i * 4 + 1] < 100);
            Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[i * 4] > 170 && pixels[i * 4 + 1] < 100 && pixels[i * 4 + 2] < 100);
        }
    }
    private static async Task<byte[]> Png()
    {
        using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, 2, 2, 96, 96, Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, 4).SelectMany(p => p).ToArray());
        await encoder.FlushAsync(); stream.Seek(0); var bytes = new byte[stream.Size]; using var reader = new global::Windows.Storage.Streams.DataReader(stream); await reader.LoadAsync((uint)bytes.Length); reader.ReadBytes(bytes); return bytes;
    }
}
