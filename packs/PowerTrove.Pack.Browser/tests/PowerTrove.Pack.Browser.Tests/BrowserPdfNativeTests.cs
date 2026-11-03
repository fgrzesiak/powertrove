using System.Text;
using PowerTrove.Abstractions.Files;
using PowerTrove.Pack.Browser;

namespace PowerTrove.Pack.Browser.Tests;

/// <summary>Real Microsoft Edge printing of a browser-saved page, as the installed pack does it.</summary>
public sealed class BrowserPdfNativeTests
{
    // A 1x1 PNG; the archive also references an external image that must never load.
    private const string Dot = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private static byte[] Archive(bool withImage = true)
    {
        string Part(string type, string location, string base64) =>
            "------Boundary\r\nContent-Type: " + type + "\r\nContent-Transfer-Encoding: base64\r\n" +
            "Content-Location: " + location + "\r\n\r\n" + base64 + "\r\n";
        string html = "<!doctype html><html><head><title>Archived</title>" +
            "<link rel=\"stylesheet\" href=\"https://example.org/style.css\"></head><body>" +
            "<h1>Archived page</h1>" + (withImage
                ? "<img src=\"https://example.org/dot.png\" width=\"80\" height=\"80\">"
                // A real, reachable image: it may only appear if the network were not blocked.
                // Without a size and with empty alt text, a blocked image draws no placeholder.
                : "<img alt=\"\" src=\"https://www.powertrove.net/icon.png\">") +
            "</body></html>";
        string b64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return Encoding.ASCII.GetBytes(
            "From: <Saved by Blink>\r\nSnapshot-Content-Location: https://example.org/\r\nSubject: Archived\r\n" +
            "MIME-Version: 1.0\r\nContent-Type: multipart/related;\r\n\ttype=\"text/html\";\r\n\tboundary=\"----Boundary\"\r\n\r\n" +
            Part("text/html", "https://example.org/", b64(html)) +
            Part("text/css", "https://example.org/style.css", b64("h1{color:#006bff;font-family:Georgia}")) +
            (withImage ? Part("image/png", "https://example.org/dot.png", Dot) : "") +
            "------Boundary--\r\n");
    }

    private sealed class ArchivePage(bool withImage) : IBrowserPageService
    {
        public Task<BrowserPageContent?> FetchAsync(BrowserPageInfo page, BrowserPageOperation operation,
            CancellationToken token) =>
            Task.FromResult<BrowserPageContent?>(operation == BrowserPageOperation.CaptureMhtml
                ? new(operation, Binary: Archive(withImage)) : null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EdgePrintsTheSavedPageWithOnlyItsArchivedImages(bool withImage)
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Path.Combine(Path.GetTempPath(), "powertrove-browser-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var fs = new PhysicalOutputFileSystem();
            var action = new BrowserFileExportAction("capture-page-pdf", new ArchivePage(withImage), fs, new OutputPlanner(fs));
            var now = DateTimeOffset.UtcNow;
            var context = new ContextSnapshot(Guid.NewGuid(),
                new SourceWindowIdentity(IntPtr.Zero, 0, "msedge", now, "Archived"),
                ContextStatus.Available, new TextSelection("https://example.org/"), now, now.AddMinutes(2),
                "powertrove.provider.browser.explicit", "https://example.org/")
            {
                BrowserPage = new BrowserPageInfo("https://example.org/", "Archived", "example.org",
                    false, 0, true, 0, 0, 0, 2, 0, "en", 2)
                { Tab = new BrowserTabHandle(Guid.NewGuid(), 1, 2, "doc") }
            };
            var receipt = await action.ExecuteAsync(context,
                new DictionaryParameters(new Dictionary<string, string> { ["OutputDirectory"] = dir }), null, default);

            Assert.True(receipt.IsSuccess, "Microsoft Edge is required for this native test.");
            string output = Assert.Single(receipt.OutputPaths);
            Assert.Equal("Archived.pdf", Path.GetFileName(output));
            byte[] pdf = await File.ReadAllBytesAsync(output);
            Assert.True(pdf.AsSpan().StartsWith("%PDF-"u8));
            // Pictures come only from inside the archive; nothing is fetched from the web.
            Assert.Equal(withImage, pdf.AsSpan().IndexOf("/Subtype /Image"u8) >= 0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
