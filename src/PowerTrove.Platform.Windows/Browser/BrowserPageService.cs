using System.Text.Json;
using PowerTrove.Abstractions;

namespace PowerTrove.Platform.Windows.Browser;

/// <summary>
/// Converts strictly bounded, tab-bound Chromium responses into typed pack inputs.
/// Operations fail closed on navigation, missing permissions or oversized data.
/// </summary>
public sealed class BrowserPageService : IBrowserPageService
{
    private readonly Func<BrowserTabHandle, string, CancellationToken, Task<JsonElement?>> _request;
    private readonly Func<BrowserTabHandle, string, CancellationToken, Task<byte[]?>>? _binaryRequest;

    public BrowserPageService(BrowserSessionServer server)
        : this(server.FetchAsync, server.FetchBinaryAsync) { }

    public BrowserPageService(Func<BrowserTabHandle, string, CancellationToken, Task<JsonElement?>> request,
        Func<BrowserTabHandle, string, CancellationToken, Task<byte[]?>>? binaryRequest = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _binaryRequest = binaryRequest;
    }

    public async Task<BrowserPageContent?> FetchAsync(BrowserPageInfo page,
        BrowserPageOperation operation, CancellationToken cancellationToken)
    {
        if (page is null || page.Tab is null) return null;
        if (operation is BrowserPageOperation.CaptureVisiblePng
            or BrowserPageOperation.CaptureFullPng or BrowserPageOperation.CaptureMhtml)
        {
            if (_binaryRequest is null) return null;
            string action = operation switch
            {
                BrowserPageOperation.CaptureVisiblePng => "captureVisible",
                BrowserPageOperation.CaptureFullPng => "captureFullPage",
                _ => "saveMhtml"
            };
            byte[]? bytes = await _binaryRequest(page.Tab, action, cancellationToken)
                .ConfigureAwait(false);
            long maximum = operation == BrowserPageOperation.CaptureMhtml
                ? 64L * 1024 * 1024 : 24L * 1024 * 1024;
            return bytes is { Length: > 0 } && bytes.LongLength <= maximum
                ? new BrowserPageContent(operation, Binary: bytes) : null;
        }
        var operationName = operation switch
        {
            BrowserPageOperation.ArticleMarkdown => "extractArticle",
            BrowserPageOperation.PageMarkdown => "extractPage",
            BrowserPageOperation.Tables => "extractTables",
            BrowserPageOperation.Links => "listLinks",
            BrowserPageOperation.Images => "listImages",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        var raw = await _request(page.Tab, operationName, cancellationToken).ConfigureAwait(false);
        if (raw is null) return null;
        try
        {
            if (operation is BrowserPageOperation.ArticleMarkdown or BrowserPageOperation.PageMarkdown)
            {
                if (raw.Value.ValueKind != JsonValueKind.String) return null;
                string? markdown = raw.Value.GetString();
                return markdown is { Length: > 0 and <= 250000 }
                    ? new BrowserPageContent(operation, Markdown: markdown) : null;
            }
            if (raw.Value.ValueKind != JsonValueKind.Array) return null;
            if (operation == BrowserPageOperation.Tables)
            {
                if (raw.Value.GetArrayLength() > 30) return null;
                var tables = new List<BrowserTable>();
                foreach (var item in raw.Value.EnumerateArray())
                {
                    var rows = item.GetProperty("data");
                    if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 5000) return null;
                    var result = new List<IReadOnlyList<string>>();
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() > 200) return null;
                        var cells = new List<string>();
                        foreach (var value in row.EnumerateArray())
                        {
                            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 4096 } cell)
                                return null;
                            cells.Add(cell);
                        }
                        result.Add(cells.AsReadOnly());
                    }
                    tables.Add(new BrowserTable(result));
                }
                return new BrowserPageContent(operation, Tables: tables);
            }
            if (operation == BrowserPageOperation.Links && raw.Value.GetArrayLength() <= 200)
            {
                var links = new List<BrowserLink>();
                foreach (var item in raw.Value.EnumerateArray())
                {
                    string? title = item.GetProperty("title").GetString();
                    string? url = item.GetProperty("url").GetString();
                    if (title is null || title.Length > 150 || !SafeUrl(url)) return null;
                    links.Add(new BrowserLink(title, url!));
                }
                return new BrowserPageContent(operation, Links: links);
            }
            if (operation == BrowserPageOperation.Images && raw.Value.GetArrayLength() <= 100)
            {
                var images = new List<BrowserImage>();
                foreach (var item in raw.Value.EnumerateArray())
                {
                    string? url = item.GetProperty("url").GetString();
                    string? alt = item.GetProperty("alt").GetString();
                    int width = item.GetProperty("width").GetInt32();
                    int height = item.GetProperty("height").GetInt32();
                    if (!SafeUrl(url) || alt is null || alt.Length > 120 ||
                        width is < 1 or > 100000 || height is < 1 or > 100000) return null;
                    images.Add(new BrowserImage(url!, alt, width, height));
                }
                return new BrowserPageContent(operation, Images: images);
            }
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException) { return null; }
    }

    private static bool SafeUrl(string? input) => input is { Length: > 0 and <= 2048 } &&
        Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
        uri.Scheme is ("http" or "https") && uri.UserInfo.Length == 0;
}
