namespace PowerTrove.Pack.Browser;

/// <summary>
/// Desktop actions for browser selections delivered through the explicit native bridge.
/// Browser-native exports (screenshots/MHTML/article Markdown) live in the MV3 extension.
/// </summary>
public sealed class BrowserPack : IActionPack
{
    public ActionPackInfo Info { get; } = new("powertrove.browser", SR.Get("Pack"), PowerTrove.Packs.PackReleaseVersion.Read(typeof(BrowserPack).Assembly), "PowerTrove");
    public BrowserPack(IBrowserPageService? pages = null,
        PowerTrove.Abstractions.Files.IOutputFileSystem? files = null,
        PowerTrove.Abstractions.Files.IOutputPlanner? planner = null)
    {
        Actions =
        [
            new BrowserTextAction("copy-selection"),
        new BrowserTextAction("quote-selection"),
        new BrowserTextAction("copy-page-link"),
        new BrowserTextAction("copy-page-url"),
        new BrowserTextAction("copy-page-link-html"),
        new BrowserTextAction("quote-with-source"),
        new BrowserDataAction("article-markdown", pages),
        new BrowserDataAction("page-markdown", pages),
        new BrowserDataAction("links-markdown", pages),
        new BrowserDataAction("images-markdown", pages),
        new BrowserDataAction("table-csv", pages),
        new BrowserFileExportAction("save-article-md", pages, files, planner),
        new BrowserFileExportAction("save-page-md", pages, files, planner),
        new BrowserFileExportAction("save-links-md", pages, files, planner),
        new BrowserFileExportAction("save-links-csv", pages, files, planner),
        new BrowserFileExportAction("save-table-csv", pages, files, planner),
        new BrowserFileExportAction("capture-visible-png", pages, files, planner),
        new BrowserFileExportAction("capture-full-png", pages, files, planner),
        new BrowserFileExportAction("capture-page-mhtml", pages, files, planner),
        new BrowserFileExportAction("capture-page-pdf", pages, files, planner)
        ];
    }
    public IReadOnlyList<IAction> Actions { get; }
}

public sealed class BrowserTextAction : IAction
{
    private readonly string _operation;
    public BrowserTextAction(string operation)
    {
        if (operation is not ("copy-selection" or "quote-selection" or "copy-page-link" or
            "copy-page-url" or "copy-page-link-html" or "quote-with-source"))
            throw new ArgumentException("Unknown browser action.", nameof(operation));
        _operation = operation;
    }

    public ActionDescriptor Descriptor => new(
        "powertrove.browser." + _operation,
        _operation switch
        {
            "copy-selection" => SR.Get("CopySelection"),
            "quote-selection" => SR.Get("QuoteSelection"),
            "copy-page-url" => SR.Get("CopyPageUrl"),
            "copy-page-link-html" => SR.Get("CopyPageLinkHtml"),
            "quote-with-source" => SR.Get("QuoteWithSource"),
            _ => SR.Get("CopyPageLink")
        },
        SR.Get("Description"),
        new HashSet<ContentKind> { ContentKind.Text },
        new[] { "browser", "selection", "link", "markdown", "copy" },
        RequiresPreview: true, ChangesFiles: false,
        CanRememberParameters: false);

    private bool Available(ContextSnapshot context) =>
        context.Status == ContextStatus.Available && !context.IsStale &&
        context.ProviderId == "powertrove.provider.browser.explicit" &&
        context.Content is TextSelection { Text.Length: > 0 and <= 24576 } &&
        (_operation is "copy-selection" or "quote-selection" ||
         Uri.TryCreate(context.UserMessage, UriKind.Absolute, out var url) &&
         url.Scheme is ("https" or "http") && url.UserInfo.Length == 0) &&
        (_operation != "quote-with-source" ||
         ((TextSelection)context.Content).Text != context.UserMessage);

    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ActionAvailability(Available(context)));
    }

    public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Available(context)) return ValueTask.FromResult(new ActionPreview(false, SR.Get("Unavailable"), [], [], IsBlocked: true));
        string value = Output(context);
        return ValueTask.FromResult(new ActionPreview(true,
            SR.Get("Preview") + "\n" + (value.Length > 320 ? value[..320] + "…" : value),
            [], []));
    }

    public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters,
        IProgress<ActionProgress>? progress, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromResult(new ActionReceipt(false, true, [], [], false));
        if (!Available(context)) return Task.FromResult(new ActionReceipt(false, false, [], [], false, SR.Get("Unavailable")));
        string value = Output(context);
        return Task.FromResult(new ActionReceipt(true, false, [], [], false, SR.Get("Done"), ClipboardText: value));
    }

    private string Output(ContextSnapshot context)
    {
        string text = ((TextSelection)context.Content).Text;
        return _operation switch
        {
            "copy-selection" => text,
            "quote-selection" => Quote(text),
            "quote-with-source" => Quote(text) + "\n\n— [" +
                Escape(context.Source.Title is { Length: > 0 } sourceTitle ? sourceTitle : context.UserMessage!) +
                "](" + context.UserMessage + ") · " +
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            "copy-page-url" => context.UserMessage!,
            "copy-page-link-html" => "<a href=\"" + Html(context.UserMessage!) + "\">" +
                Html(context.Source.Title is { Length: > 0 } htmlTitle ? htmlTitle : context.UserMessage!) + "</a>",
            _ => "[" + Escape(context.Source.Title is { Length: > 0 } title ? title : context.UserMessage!) +
                 "](" + context.UserMessage + ")"
        };
    }
    private static string Quote(string text) => string.Join("\n",
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => "> " + line));
    private static string Html(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
}
