using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using FileKit.Html;
using PowerTrove.Pack.Pdf.Html;
using PowerTrove.Pack.Pdf.Windows;
using Windows.Data.Pdf;
using Windows.Storage;

namespace PowerTrove.Pack.Pdf.Windows.Tests;

public sealed class HtmlPdfRendererTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    [Fact]
    public async Task ContainedBrowserPrintsStyledHtmlWithEmbeddedImageAndConfiguredPaper()
    {
        var renderer = Renderer(); Assert.True(renderer.IsAvailable);
        string image = Convert.ToBase64String(await Png());
        var document = Document("<html><head><style>h1{color:red}p{font-size:18px} .break{break-before:page}</style></head><body><h1>Print test</h1><p>Grüße</p><img width='100' height='100' src='data:image/png;base64," + image + "'><p class='break'>Second page</p></body></html>");
        string output = _directory.File("printed.pdf");
        await renderer.RenderAsync(document, output, new("letter", true, 12), default);
        Assert.True(File.ReadAllBytes(output).AsSpan().StartsWith("%PDF-"u8));
        var pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(output));
        Assert.Equal(2u, pdf.PageCount);
        using var page = pdf.GetPage(0);
        // Windows reports PDF page dimensions in 96-DPI DIPs: 11 × 8.5 inches.
        Assert.InRange(page.Size.Width, 1054, 1058); Assert.InRange(page.Size.Height, 814, 818);
        using var png = new global::Windows.Storage.Streams.InMemoryRandomAccessStream(); await page.RenderToStreamAsync(png);
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(png);
        var pixels = await decoder.GetPixelDataAsync(); byte[] values = pixels.DetachPixelData();
        Assert.Contains(Enumerable.Range(0, values.Length / 4), i => values[i * 4 + 2] > 170 && values[i * 4] < 100 && values[i * 4 + 1] < 100);
        Assert.Contains(Enumerable.Range(0, values.Length / 4), i => values[i * 4] > 170 && values[i * 4 + 1] < 100 && values[i * 4 + 2] < 100);
    }
    [Fact]
    public async Task CancellationAfterStartingBrowserTerminatesItAndRemovesItsProfile()
    {
        using var cancellation = new CancellationTokenSource();
        var launcher = new TrackingLauncher(cancellation);
        var writer = new ChromiumPdfWriter(launcher); string output = _directory.File("cancelled-after-start.pdf");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(Document("<h1>test</h1>"), output, new(), cancellation.Token));
        Assert.True(launcher.Started); Assert.True(launcher.Stopped);
        Assert.False(File.Exists(output)); Assert.False(Directory.Exists(launcher.Profile));
    }
    private sealed class TrackingLauncher(CancellationTokenSource cancellation) : IHtmlBrowserLauncher
    {
        private readonly WindowsHtmlBrowserLauncher _launcher = new();
        public bool Started { get; private set; } public bool Stopped { get; private set; } public string? Profile { get; private set; }
        public bool IsAvailable => _launcher.IsAvailable;
        public IHtmlBrowserProcess Start(IReadOnlyList<string> arguments)
        {
            var process = _launcher.Start(arguments); Started = true;
            Profile = arguments.Single(a => a.StartsWith("--user-data-dir=", StringComparison.Ordinal))["--user-data-dir=".Length..];
            cancellation.Cancel(); return new Tracked(process, () => Stopped = process.HasExited);
        }
        private sealed class Tracked(IHtmlBrowserProcess inner, Action stopped) : IHtmlBrowserProcess
        {
            public bool HasExited => inner.HasExited;
            public void Dispose() { inner.Dispose(); stopped(); }
        }
    }
    [Fact]
    public async Task UntrustedHtmlCannotFetchNetworkAndActiveMarkupIsRefused()
    {
        // Edge printing on hosted GitHub runners is unreliable; this runs on real Windows desktops.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") return;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; string probe = $"http://127.0.0.1:{port}/forbidden";
            Assert.Throws<HtmlPrintException>(() => Document($"<script>fetch('{probe}')</script><h1>Safe content</h1>"));
            Assert.Throws<HtmlPrintException>(() => Document($"<meta http-equiv='refresh' content='0;url={probe}'>"));
            var document = Document($"<style>@import url('{probe}');p{{background-image:url('{probe}')}}</style><h1>Safe content</h1><img src='{probe}'><img src='file:///unavailable-resource'>");
            string output = _directory.File("restricted.pdf");
            await Renderer().RenderAsync(document, output, new(), default);
            Assert.False(listener.Pending());
            var pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(output)); Assert.Equal(1u, pdf.PageCount);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            string absent = _directory.File("cancelled.pdf");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Renderer().RenderAsync(document, absent, new(), cancelled.Token));
            Assert.False(File.Exists(absent));
        }
        finally { listener.Stop(); }
    }
    private static WindowsHtmlPdfRenderer Renderer() => new(new SafeHtmlMarkupValidator());
    private static HtmlPrintDocument Document(string html) => HtmlPrintDocument.FromText(html, new SafeHtmlMarkupValidator());
    private static async Task<byte[]> Png()
    {
        using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        byte[] pixels = Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, 4).SelectMany(p => p).ToArray();
        encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, 2, 2, 96, 96, pixels);
        await encoder.FlushAsync(); stream.Seek(0); var bytes = new byte[stream.Size]; await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, global::Windows.Storage.Streams.InputStreamOptions.None); return bytes;
    }
}
