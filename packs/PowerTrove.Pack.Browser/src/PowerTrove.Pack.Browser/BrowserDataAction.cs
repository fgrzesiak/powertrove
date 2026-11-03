namespace PowerTrove.Pack.Browser;

/// <summary>
/// Browser-specific work requests are mediated by the host. Packs never access
/// Chromium directly, and the host binds requests to a short-lived tab snapshot.
/// </summary>
public sealed class BrowserDataAction : IAction
{
    private readonly string _operation;
    private readonly IBrowserPageService? _pages;

    public BrowserDataAction(string operation, IBrowserPageService? pages)
    {
        if (operation is not ("article-markdown" or "page-markdown" or "links-markdown"
            or "images-markdown" or "table-csv"))
            throw new ArgumentException("Unknown browser data action.", nameof(operation));
        _operation = operation;
        _pages = pages;
    }

    public ActionDescriptor Descriptor => new("powertrove.browser." + _operation,
        _operation switch
        {
            "article-markdown" => SR.Get("ArticleMarkdown"),
            "page-markdown" => SR.Get("PageMarkdown"),
            "links-markdown" => SR.Get("LinksMarkdown"),
            "images-markdown" => SR.Get("ImagesMarkdown"),
            _ => SR.Get("TableCsv")
        },
        SR.Get("DataDescription"),
        new HashSet<ContentKind> { ContentKind.Text },
        // Per action, so a search for one task does not list every page action.
        _operation switch
        {
            "article-markdown" => ["browser", "article", "markdown", "text", "reader", "clipboard"],
            "page-markdown" => ["browser", "page", "website", "markdown", "text", "clipboard"],
            "links-markdown" => ["browser", "links", "urls", "markdown", "list", "clipboard"],
            "images-markdown" => ["browser", "images", "pictures", "urls", "markdown", "clipboard"],
            _ => ["browser", "table", "csv", "excel", "spreadsheet", "data", "clipboard"]
        },
        RequiresPreview: true, ChangesFiles: false, CanRememberParameters: false);

    private bool Available(ContextSnapshot context) => _pages is not null &&
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
            ? new ActionPreview(true, SR.Get("ReadOnConfirm"), [], [])
            : new ActionPreview(false, SR.Get("Unavailable"), [], [], IsBlocked:true));
    }

    public async Task<ActionReceipt> ExecuteAsync(ContextSnapshot context,
        ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Available(context)) return Failure();
        BrowserPageOperation kind = _operation switch
        {
            "article-markdown" => BrowserPageOperation.ArticleMarkdown,
            "page-markdown" => BrowserPageOperation.PageMarkdown,
            "links-markdown" => BrowserPageOperation.Links,
            "images-markdown" => BrowserPageOperation.Images,
            _ => BrowserPageOperation.Tables
        };
        BrowserPageContent? data = await _pages!.FetchAsync(context.BrowserPage!, kind, token);
        if (data is null) return Failure();

        string? text = _operation switch
        {
            "article-markdown" or "page-markdown" => data.Markdown,
            "links-markdown" => data.Links is { Count: > 0 } links
                ? string.Join("\n",links.Select(link => "- [" + Escape(link.Title) + "](" + link.Url + ")")) : null,
            "images-markdown" => data.Images is { Count: > 0 } images
                ? string.Join("\n",images.Select(image => "![" + Escape(image.Alt) + "](" + image.Url + ")")) : null,
            _ => data.Tables is { Count: > 0 } tables
                ? string.Join("\r\n",tables[0].Rows.Select(row => string.Join(",",
                    row.Select(CsvCell)))) : null
        };
        if (string.IsNullOrWhiteSpace(text) || text.Length > 250000) return Failure();
        return new ActionReceipt(true,false,[],[],false,SR.Get("Done"),ClipboardText:text);
    }

    private static ActionReceipt Failure() => new(false,false,[],[],false,SR.Get("DataUnavailable"));
    private static string Escape(string value) =>
        value.Replace("\\","\\\\",StringComparison.Ordinal)
            .Replace("[","\\[",StringComparison.Ordinal).Replace("]","\\]",StringComparison.Ordinal);
    private static string CsvCell(string value)
    {
        string safe = value;
        if (safe.Length > 0 && (safe[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
            safe = "'" + safe;
        return "\"" + safe.Replace("\"","\"\"",StringComparison.Ordinal) + "\"";
    }
}
