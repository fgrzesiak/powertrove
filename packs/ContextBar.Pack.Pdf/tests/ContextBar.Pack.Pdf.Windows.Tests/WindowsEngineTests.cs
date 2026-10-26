using ContextBar.Pack.Pdf;
using ContextBar.Pack.Pdf.Windows;
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
    public async Task PdfRenderer_RendersSelectedPages()
    {
        if (!OperatingSystem.IsWindows()) return;
        string pdf = _dir.Write("doc.pdf", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "linked-objstm.pdf")));
        string output = Directory.CreateDirectory(_dir.File("out")).FullName;
        var renderer = new WindowsPdfRenderer();

        Assert.Equal(3, await renderer.GetPageCountAsync(pdf, CancellationToken.None));
        await renderer.RenderToDirectoryAsync(pdf, output, "doc", new[] { 0, 2 }, new PdfRenderOptions("png", 72), null, CancellationToken.None);

        var files = Directory.GetFiles(output).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "doc - page 01.png", "doc - page 03.png" }, files);
        var decoder = await DecodeAsync(File.ReadAllBytes(Path.Combine(output, "doc - page 01.png")));
        Assert.InRange((int)decoder.PixelWidth, 415, 425); // A5 at 72 DPI = 420 px
    }

    [Fact]
    public async Task PdfRenderer_DamagedFile_ReportsSafeError()
    {
        if (!OperatingSystem.IsWindows()) return;
        string pdf = _dir.Write("broken.pdf", "%PDF-1.7 nope");
        var ex = await Assert.ThrowsAsync<FileProcessingException>(() => new WindowsPdfRenderer().GetPageCountAsync(pdf, CancellationToken.None));
        Assert.Equal("The PDF could not be opened.", ex.Message);
    }

    [Fact]
    public async Task PdfRenderer_RenderGrayAsync_DetectsBlankPage()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Use an unambiguous filled page and a blank control, rather than sparse
        // fixture text that can legitimately fall below the blankness threshold.
        byte[] png = await EncodeAsync(BitmapEncoder.PngEncoderId, 80, 60);
        string pdfPath = _dir.File("blank-detection.pdf");
        using (var output = File.Create(pdfPath))
            new FileKit.Pdf.PdfDocumentBuilder()
                .AddImagePage(FileKit.Pdf.PdfImage.Load(png), new FileKit.Pdf.PdfImagePageLayout(FileKit.Pdf.PdfPageSize.FitImage))
                .AddBlankPage(60, 45).Save(output);
        var renderer = new WindowsPdfRenderer();
        var raster0 = await renderer.RenderGrayAsync(pdfPath, 0, 150, CancellationToken.None);
        Assert.NotNull(raster0.Gray8);
        Assert.True(raster0.Width <= 150);
        bool isBlank = ContextBar.Pack.Pdf.BlankPageDetector.IsBlank(raster0, "normal");
        Assert.False(isBlank);
        Assert.Equal(raster0.Width * raster0.Height, raster0.Gray8.Length);
        Assert.Contains(raster0.Gray8, pixel => pixel < 200);
        var blank = await renderer.RenderGrayAsync(pdfPath, 1, 150, CancellationToken.None);
        Assert.True(BlankPageDetector.IsBlank(blank, "normal"));
    }
}
