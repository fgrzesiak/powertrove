using System.Text;
using PowerTrove.Abstractions.Files;

namespace PowerTrove.Pack.Browser;

/// <summary>
/// Writes browser data via the worker's existing staging file system and the
/// host's collision-free commit. Never writes arbitrary browser-supplied paths.
/// </summary>
public sealed class BrowserFileExportAction : IAction
{
    private readonly string _mode;
    private readonly IBrowserPageService? _pages;
    private readonly IOutputFileSystem? _files;
    private readonly IOutputPlanner? _planner;
    private readonly Func<byte[], CancellationToken, Task<byte[]>> _renderPdf;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public BrowserFileExportAction(string mode, IBrowserPageService? pages,
        IOutputFileSystem? files, IOutputPlanner? planner,
        Func<byte[], CancellationToken, Task<byte[]>>? renderPdf = null)
    {
        if (mode is not ("save-article-md" or "save-page-md" or "save-links-md"
            or "save-links-csv" or "save-table-csv" or "capture-visible-png"
            or "capture-full-png" or "capture-page-mhtml" or "capture-page-pdf"))
            throw new ArgumentException("Unknown browser file export.", nameof(mode));
        _mode = mode;
        _pages = pages;
        _files = files;
        _planner = planner;
        _renderPdf = renderPdf ?? BrowserPdfPrint.RenderArchiveAsync;
    }

    public ActionDescriptor Descriptor => new("powertrove.browser." + _mode,
        _mode switch
        {
            "save-article-md" => SR.Get("SaveArticleMarkdown"),
            "save-page-md" => SR.Get("SavePageMarkdown"),
            "save-links-md" => SR.Get("SaveLinksMarkdown"),
            "save-links-csv" => SR.Get("SaveLinksCsv"),
            "capture-visible-png" => SR.Get("CaptureVisiblePng"),
            "capture-full-png" => SR.Get("CaptureFullPng"),
            "capture-page-mhtml" => SR.Get("CapturePageMhtml"),
            "capture-page-pdf" => SR.Get("CapturePagePdf"),
            _ => SR.Get("SaveTableCsv")
        },
        SR.Get("FileExportDescription"),
        new HashSet<ContentKind> { ContentKind.Text },
        // Per action: "screenshot" or "pdf" must find the capture, not every page export.
        _mode switch
        {
            "save-article-md" => ["browser", "article", "markdown", "save", "file", "reader"],
            "save-page-md" => ["browser", "page", "website", "markdown", "save", "file"],
            "save-links-md" => ["browser", "links", "urls", "markdown", "save", "file"],
            "save-links-csv" => ["browser", "links", "urls", "csv", "save", "file"],
            "capture-visible-png" => ["browser", "screenshot", "capture", "png", "image", "picture", "visible"],
            "capture-full-png" => ["browser", "screenshot", "capture", "png", "image", "full page", "scrolling"],
            "capture-page-mhtml" => ["browser", "mhtml", "offline", "archive", "save page", "web archive"],
            "capture-page-pdf" => ["browser", "pdf", "print", "save as pdf", "page", "document"],
            _ => ["browser", "table", "csv", "excel", "spreadsheet", "save", "file"]
        },
        RequiresPreview: true, ChangesFiles: true, CanRememberParameters: false);

    /// <summary>Names the file after the page, like a browser download; the planner removes
    /// characters Windows does not allow and adds a number instead of overwriting.</summary>
    internal static string OutputBaseName(BrowserPageInfo page, string mode)
    {
        string title = string.Join(" ", page.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length > 80) title = title[..80].TrimEnd();
        if (title.Length == 0) title = page.Domain;
        string? suffix = mode switch
        {
            "save-article-md" => SR.Get("FileSuffixArticle"),
            "save-links-md" or "save-links-csv" => SR.Get("FileSuffixLinks"),
            "save-table-csv" => SR.Get("FileSuffixTable"),
            "capture-visible-png" => SR.Get("FileSuffixScreenshot"),
            "capture-full-png" => SR.Get("FileSuffixFullPage"),
            _ => null
        };
        return suffix is null ? title : title + " (" + suffix + ")";
    }

    private bool Available(ContextSnapshot context) => _pages is not null &&
        _files is not null && _planner is not null &&
        context.Status == ContextStatus.Available && !context.IsStale &&
        context.ProviderId == "powertrove.provider.browser.explicit" &&
        context.BrowserPage?.Tab is not null;

    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ActionAvailability(Available(context)));
    }

    public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context,
        ActionParameters parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Available(context)
            ? new ActionPreview(true, SR.Get("FileExportPreview"), [], [])
            : new ActionPreview(false, SR.Get("Unavailable"), [], [], IsBlocked:true));
    }

    public async Task<ActionReceipt> ExecuteAsync(ContextSnapshot context,
        ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Available(context)) return Failed();
        var operation = _mode switch
        {
            "save-article-md" => BrowserPageOperation.ArticleMarkdown,
            "save-page-md" => BrowserPageOperation.PageMarkdown,
            "save-links-md" or "save-links-csv" => BrowserPageOperation.Links,
            "capture-visible-png" => BrowserPageOperation.CaptureVisiblePng,
            "capture-full-png" => BrowserPageOperation.CaptureFullPng,
            "capture-page-mhtml" => BrowserPageOperation.CaptureMhtml,
            // The PDF is printed from the page as the browser saved it, with layout and images.
            "capture-page-pdf" => BrowserPageOperation.CaptureMhtml,
            _ => BrowserPageOperation.Tables
        };
        var page = await _pages!.FetchAsync(context.BrowserPage!, operation, token);
        if (page is null) return Failed();
        byte[]? printedPdf = null;
        if (_mode == "capture-page-pdf")
        {
            if (page.Binary is not { Length: > 0 } archive) return Failed();
            try { printedPdf = await _renderPdf(archive, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or FileKit.Html.HtmlPrintException or InvalidOperationException) { return Failed(); }
        }
        bool binary = _mode == "capture-page-pdf" ||
            operation is BrowserPageOperation.CaptureVisiblePng
                or BrowserPageOperation.CaptureFullPng or BrowserPageOperation.CaptureMhtml;
        string? text = operation switch
        {
            BrowserPageOperation.ArticleMarkdown or BrowserPageOperation.PageMarkdown => page.Markdown,
            BrowserPageOperation.Links when page.Links is { Count: > 0 } links =>
                _mode == "save-links-csv"
                    ? "Title,URL\r\n" + string.Join("\r\n",links.Select(l=>
                        Csv(l.Title) + "," + Csv(l.Url))) + "\r\n"
                    : string.Join("\n",links.Select(l=>"- [" + Markdown(l.Title) + "](" + l.Url + ")")),
            BrowserPageOperation.Tables when page.Tables is { Count: > 0 } tables =>
                string.Join("\r\n", tables[0].Rows.Select(row=>string.Join(",",row.Select(Csv))))+"\r\n",
            _ => null
        };
        if (binary)
        {
            byte[]? payload = _mode == "capture-page-pdf" ? printedPdf : page.Binary;
            if (payload is not { Length: > 0 } raw) return Failed();
            int max = operation == BrowserPageOperation.CaptureMhtml
                ? 64 * 1024 * 1024 : 24 * 1024 * 1024;
            if (raw.Length > max) return Failed();
            if (_mode == "capture-page-pdf" &&
                (raw.Length < 5 || !raw.AsSpan(0,5).SequenceEqual("%PDF-"u8)))
                return Failed();
            if (operation == BrowserPageOperation.CaptureMhtml && _mode != "capture-page-pdf" &&
                !Encoding.ASCII.GetString(raw.AsSpan(0, Math.Min(4096,raw.Length)))
                    .Contains("MIME-Version:", StringComparison.OrdinalIgnoreCase))
                return Failed();
            if ((operation is BrowserPageOperation.CaptureVisiblePng or BrowserPageOperation.CaptureFullPng) &&
                (raw.Length < 8 || !raw.AsSpan(0,8).SequenceEqual(
                    new byte[] {137,80,78,71,13,10,26,10}))) return Failed();
        }
        else if (string.IsNullOrWhiteSpace(text) || text.Length > 250000) return Failed();

        string directory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (parameters is DictionaryParameters dictionary &&
            dictionary.Values.TryGetValue("OutputDirectory", out var requested) &&
            Path.IsPathFullyQualified(requested) && _files!.DirectoryExists(requested))
            directory = Path.GetFullPath(requested);
        if (string.IsNullOrWhiteSpace(directory) || !_files!.DirectoryExists(directory))
            return Failed();

        string extension = _mode.EndsWith("-csv",StringComparison.Ordinal) ? ".csv" :
            _mode == "capture-page-pdf" ? ".pdf" :
            operation == BrowserPageOperation.CaptureMhtml ? ".mhtml" :
            binary ? ".png" : ".md";
        var writer = new FileOutputWriter(_files, _planner!);
        try
        {
            string target = await writer.WriteFileAsync(directory,
                OutputBaseName(context.BrowserPage!, _mode), extension,
                async (stream, ct) =>
                {
                    byte[] bytes = binary ? (_mode == "capture-page-pdf" ? printedPdf! : page.Binary!) : Utf8.GetBytes(text!);
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
            return new ActionReceipt(true,false,[target],[],false,SR.Get("FileExportDone"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return Failed(); }
    }
    private static string Markdown(string value) => value.Replace("\\","\\\\",
        StringComparison.Ordinal).Replace("[","\\[",StringComparison.Ordinal)
        .Replace("]","\\]",StringComparison.Ordinal);
    private static string Csv(string value)
    {
        string safe = value;
        string leading = safe.TrimStart();
        if (leading.Length > 0 && leading[0] is '=' or '+' or '-' or '@')
            safe = "'" + safe;
        return "\"" + safe.Replace("\"","\"\"",StringComparison.Ordinal) + "\"";
    }
    private static ActionReceipt Failed() =>
        new(false,false,[],[],false,SR.Get("DataUnavailable"));
}
