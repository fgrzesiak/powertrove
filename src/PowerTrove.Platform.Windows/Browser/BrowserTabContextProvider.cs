using PowerTrove.Abstractions;
using PowerTrove.Core;

namespace PowerTrove.Platform.Windows.Browser;

/// <summary>
/// Opt-in browser selection provider. One unambiguous current browser window wins;
/// never attach a page from another profile or an incognito session.
/// </summary>
public sealed class BrowserTabContextProvider : IContextProvider
{
    private readonly BrowserSessionServer _server;
    public const string ProviderId = "powertrove.provider.browser.explicit";
    public string Id => ProviderId;

    public BrowserTabContextProvider(BrowserSessionServer server) => _server = server;

    public ValueTask<bool> CanCaptureAsync(SourceWindowIdentity source, CancellationToken token) =>
        ValueTask.FromResult(IsBrowser(source));

    public static bool IsBrowser(SourceWindowIdentity source) =>
        source.ProcessName is not null &&
        (source.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
         source.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase));

    public async ValueTask<ContextSnapshot> CaptureAsync(Guid invocationId,
        SourceWindowIdentity source, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        ContextSnapshot Unsupported() => new(invocationId, source, ContextStatus.Unsupported, new NoContent(),
            now, now.AddSeconds(10), Id, null);

        if (!IsBrowser(source) || string.IsNullOrWhiteSpace(source.Title) || _server.ConnectedSessions == 0)
            return Unsupported();
        var pages = await _server.QueryAsync(token);
        // No fuzzy profile guessing: only one focused, matching tab/window is safe.
        var matches = pages.Where(p =>
            p.Focused && !p.Incognito &&
            (source.ProcessName!.Equals("chrome", StringComparison.OrdinalIgnoreCase)
                ? p.Browser == "chrome" : p.Browser == "edge") &&
            MatchesWindowTitle(source.Title!, p.Title)).ToArray();
        if (matches.Length != 1) return Unsupported();

        var page = matches[0];
        // Empty text selection is represented by the explicit page URL. This enables
        // page-link actions even before the rich WebPageInfo context lands in WP3.
        string text = string.IsNullOrEmpty(page.Selection) ? page.Url : page.Selection;
        var domain = new Uri(page.Url).Host;
        return new ContextSnapshot(invocationId,
            source with { Title = page.Title },
            ContextStatus.Available, new TextSelection(text), now, now.AddMinutes(2),
            Id, page.Url, new HashSet<string> { "browser.selection.read.v1" })
        {
            BrowserPage = new BrowserPageInfo(page.Url, page.Title, domain,
                !string.IsNullOrEmpty(page.Selection), page.Selection.Length, page.IsArticle,
                page.TableCount, page.LargestTableRows, page.LargestTableColumns,
                page.ImageCount, page.CodeBlockCount, page.Language, page.WordCount)
            {
                Tab = page.SessionId != Guid.Empty && page.DocumentId.Length > 0
                    ? new BrowserTabHandle(page.SessionId, page.WindowId, page.TabId, page.DocumentId)
                    : null
            }
        };
    }

    public static bool MatchesWindowTitle(string osCaption, string tabTitle)
    {
        if (string.IsNullOrWhiteSpace(osCaption) || string.IsNullOrWhiteSpace(tabTitle)) return false;
        if (tabTitle.Length > 300 || osCaption.Length > 600) return false;
        // Chrome appends " - [profile - ]Google Chrome"; Edge also inserts a localized
        // "and N more pages" before its " - [profile - ]Microsoft Edge" suffix.
        if (!osCaption.StartsWith(tabTitle, StringComparison.Ordinal)) return false;
        if (osCaption.Length == tabTitle.Length) return true;
        var rest = osCaption.AsSpan(tabTitle.Length);
        return rest[0] == ' ' && rest.IndexOf(" - ", StringComparison.Ordinal) >= 0 &&
            (rest.EndsWith("Chrome", StringComparison.Ordinal) || rest.EndsWith("Edge", StringComparison.Ordinal));
    }
}
