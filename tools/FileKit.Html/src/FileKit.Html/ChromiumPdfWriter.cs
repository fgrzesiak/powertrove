using System.Net;
using System.Text.Json;

namespace FileKit.Html;

/// <summary>Prints self-contained HTML in a new, job-contained browser profile.
/// Network and JavaScript are disabled before untrusted markup is supplied.
/// Requires an installed browser and a platform launcher with process-tree limits.</summary>
public sealed class ChromiumPdfWriter(IHtmlBrowserLauncher launcher)
{
    public const long MaxPdfBytes = 100L * 1024 * 1024;
    public bool IsAvailable => launcher.IsAvailable;
    public Task WriteAsync(HtmlPrintDocument document, string outputPath, HtmlPrintOptions options, CancellationToken ct = default) =>
        Task.Run(() => WriteCoreAsync(document, outputPath, options, ct), ct);
    private async Task WriteCoreAsync(HtmlPrintDocument document, string outputPath, HtmlPrintOptions options, CancellationToken ct)
    {
        options.Validate(); ct.ThrowIfCancellationRequested();
        if (!launcher.IsAvailable) throw new HtmlPrintException("A supported local browser is unavailable.");
        string profile = Path.Combine(Path.GetTempPath(), "FileKit-Html-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var process = launcher.Start([
                "--headless=new", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=0", "--user-data-dir=" + profile,
                "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--disable-component-update", "--disable-sync", "--disable-extensions",
                "--disable-default-apps", "--disable-client-side-phishing-detection", "--disable-crash-reporter", "--disable-breakpad",
                "--disable-features=MediaRouter,OptimizationHints,Translate", "--renderer-process-limit=2", "about:blank"
            ]);
            Uri target = await TargetAsync(profile, process, timeout.Token).ConfigureAwait(false);
            await using var cdp = new CdpConnection(); await cdp.ConnectAsync(target, timeout.Token).ConfigureAwait(false);
            await cdp.CommandAsync("Page.enable", new { }, timeout.Token).ConfigureAwait(false);
            await cdp.CommandAsync("Network.enable", new { }, timeout.Token).ConfigureAwait(false);
            await cdp.CommandAsync("Network.setBlockedURLs", new { urls = new[] { "http://*", "https://*", "file://*", "ftp://*", "ws://*", "wss://*" } }, timeout.Token).ConfigureAwait(false);
            await cdp.CommandAsync("Network.setBypassServiceWorker", new { bypass = true }, timeout.Token).ConfigureAwait(false);
            await cdp.CommandAsync("Network.setCacheDisabled", new { cacheDisabled = true }, timeout.Token).ConfigureAwait(false);
            await cdp.CommandAsync("Emulation.setScriptExecutionDisabled", new { value = true }, timeout.Token).ConfigureAwait(false);
            var frames = await cdp.CommandAsync("Page.getFrameTree", new { }, timeout.Token).ConfigureAwait(false);
            string frame = frames.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
            Task loaded = cdp.WaitForLoadAsync(timeout.Token);
            await cdp.CommandAsync("Page.setDocumentContent", new { frameId = frame, html = document.Html }, timeout.Token).ConfigureAwait(false);
            await loaded.ConfigureAwait(false);
            if (cdp.AttemptedNavigation) throw new HtmlPrintException("Automatic HTML navigation is unsupported.");
            var size = options.Inches;
            var printed = await cdp.CommandAsync("Page.printToPDF", new {
                landscape = options.Landscape, displayHeaderFooter = false, printBackground = true,
                paperWidth = size.Width, paperHeight = size.Height, marginTop = options.MarginMillimeters / 25.4,
                marginBottom = options.MarginMillimeters / 25.4, marginLeft = options.MarginMillimeters / 25.4, marginRight = options.MarginMillimeters / 25.4,
                preferCSSPageSize = false, transferMode = "ReturnAsStream"
            }, timeout.Token).ConfigureAwait(false);
            string handle = printed.GetProperty("stream").GetString()!;
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            long bytes = 0;
            while (true)
            {
                var chunk = await cdp.CommandAsync("IO.read", new { handle, size = 262144 }, timeout.Token).ConfigureAwait(false);
                string text = chunk.GetProperty("data").GetString()!;
                if (text.Length > 0 && (!chunk.TryGetProperty("base64Encoded", out var encoded) || !encoded.GetBoolean())) throw new HtmlPrintException("Invalid PDF stream encoding.");
                byte[] data = text.Length == 0 ? [] : Convert.FromBase64String(text);
                if ((bytes += data.Length) > MaxPdfBytes) throw new HtmlPrintException("PDF output size limit exceeded.");
                await output.WriteAsync(data, timeout.Token).ConfigureAwait(false);
                if (chunk.GetProperty("eof").GetBoolean()) break;
            }
            await cdp.CommandAsync("IO.close", new { handle }, timeout.Token).ConfigureAwait(false);
            if (bytes < 8 || cdp.AttemptedNavigation) throw new HtmlPrintException("Invalid or navigated print document.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HtmlPrintException("Local HTML rendering timed out."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.WebSockets.WebSocketException or JsonException or FormatException)
        { throw new HtmlPrintException("The local HTML renderer failed.", ex); }
        finally
        {
            // Browser child processes can hold profile files for several seconds after the job closes on a busy machine.
            for (int attempt = 0; attempt < 100; attempt++)
            {
                try { if (Directory.Exists(profile)) Directory.Delete(profile, true); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(100, CancellationToken.None).ConfigureAwait(false); }
            }
            if (Directory.Exists(profile)) throw new HtmlPrintException("Temporary browser profile cleanup failed.", failure: HtmlPrintFailure.Cleanup);
        }
    }
    private static async Task<Uri> TargetAsync(string profile, IHtmlBrowserProcess process, CancellationToken ct)
    {
        string active = Path.Combine(profile, "DevToolsActivePort");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 1024 * 1024 };
        for (int attempt = 0; attempt < 150; attempt++)
        {
            ct.ThrowIfCancellationRequested(); if (process.HasExited) throw new HtmlPrintException("The local browser could not start.");
            try
            {
                if (File.Exists(active))
                {
                    if (new FileInfo(active).Length > 4096) throw new HtmlPrintException("Invalid local browser endpoint.");
                    string[] lines = await File.ReadAllLinesAsync(active, ct).ConfigureAwait(false);
                    if (lines.Length > 0 && int.TryParse(lines[0], out int port) && port is > 0 and <= 65535)
                    {
                        using var response = await client.GetAsync($"http://127.0.0.1:{port}/json/list", ct).ConfigureAwait(false);
                        if (response.StatusCode != HttpStatusCode.OK) throw new HtmlPrintException("Invalid local browser response.");
                        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                        foreach (var target in document.RootElement.EnumerateArray())
                        {
                            if (target.GetProperty("type").GetString() != "page" || target.GetProperty("url").GetString() != "about:blank") continue;
                            var uri = new Uri(target.GetProperty("webSocketDebuggerUrl").GetString()!);
                            if (uri.Scheme != "ws" || uri.Host != "127.0.0.1" || uri.Port != port || !uri.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal) || uri.UserInfo.Length > 0 || uri.Query.Length > 0)
                                throw new HtmlPrintException("Unexpected local browser target.");
                            return uri;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        throw new HtmlPrintException("The local browser did not initialize.");
    }
}
