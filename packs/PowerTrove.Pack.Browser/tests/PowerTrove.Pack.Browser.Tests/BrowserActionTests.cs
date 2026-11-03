using PowerTrove.Pack.Browser;

namespace PowerTrove.Pack.Browser.Tests;

public class BrowserActionTests
{
    private static ContextSnapshot Browser(string selected) {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 0, "browser", now, "Example page"),
            ContextStatus.Available, new TextSelection(selected), now, now.AddMinutes(2),
            "powertrove.provider.browser.explicit", "https://example.org/docs");
    }

    [Theory]
    [InlineData("screenshot", "capture-visible-png", "capture-full-png")]
    [InlineData("pdf", "capture-page-pdf")]
    [InlineData("csv", "table-csv", "save-table-csv", "save-links-csv")]
    public void PageActionsAreFoundByTheWordForTheirTask(string word, params string[] expected)
    {
        // Localized titles do not always contain the word users type ("PNG speichern"), so terms carry it.
        var found = new BrowserPack().Actions
            .Where(a => a.Descriptor.SearchTerms.Contains(word, StringComparer.OrdinalIgnoreCase))
            .Select(a => a.Descriptor.Id["powertrove.browser.".Length..]);
        Assert.Equal(expected.Order(), found.Order());
    }

    [Fact]
    public async Task BrowserActionsCopyWithoutWritingFiles()
    {
        var pack = new BrowserPack();
        Assert.Equal(20, pack.Actions.Count);
        foreach (var action in pack.Actions.OfType<BrowserTextAction>())
        {
            var snapshot = Browser("one\ntwo");
            Assert.True((await action.MatchAsync(snapshot, default)).IsAvailable);
            var receipt = await action.ExecuteAsync(snapshot, new EmptyParameters(), null, default);
            Assert.True(receipt.IsSuccess);
            Assert.Empty(receipt.OutputPaths);
            Assert.NotNull(receipt.ClipboardText);
            Assert.True(action.Descriptor.RequiresPreview);
            Assert.False(action.Descriptor.ChangesFiles);
        }
    }

    [Fact]
    public async Task QuoteAndLinkAreMarkdownReady()
    {
        var snapshot = Browser("one\ntwo");
        var quote = await new BrowserTextAction("quote-selection").ExecuteAsync(snapshot, new EmptyParameters(), null, default);
        Assert.Equal("> one\n> two", quote.ClipboardText);
        var link = await new BrowserTextAction("copy-page-link").ExecuteAsync(snapshot, new EmptyParameters(), null, default);
        Assert.Equal("[Example page](https://example.org/docs)", link.ClipboardText);
    }

    [Fact]
    public async Task BrowserLinkFormatsAndSourceQuoteEscapeUntrustedTitles()
    {
        var snapshot = Browser("one\ntwo") with
        {
            Source = new SourceWindowIdentity(IntPtr.Zero,0,"chrome",DateTimeOffset.UtcNow,
                "Example <page> & \"quote\"")
        };
        var html = await new BrowserTextAction("copy-page-link-html").ExecuteAsync(snapshot,
            new EmptyParameters(),null,default);
        Assert.Equal("<a href=\"https://example.org/docs\">Example &lt;page&gt; &amp; &quot;quote&quot;</a>",html.ClipboardText);
        var url = await new BrowserTextAction("copy-page-url").ExecuteAsync(snapshot,
            new EmptyParameters(),null,default);
        Assert.Equal("https://example.org/docs",url.ClipboardText);
        var quote = await new BrowserTextAction("quote-with-source").ExecuteAsync(snapshot,
            new EmptyParameters(),null,default);
        Assert.StartsWith("> one\n> two\n\n— [Example <page> & \"quote\"]",quote.ClipboardText);
    }

    [Fact]
    public async Task NonBrowserOrStaleSelectionIsRejected()
    {
        var action = new BrowserTextAction("copy-selection");
        var current = Browser("secret");
        var stale = current with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        var other = current with { ProviderId = "powertrove.provider.windows.uia-text" };
        Assert.False((await action.MatchAsync(stale, default)).IsAvailable);
        Assert.False((await action.ExecuteAsync(other, new EmptyParameters(), null, default)).IsSuccess);
    }
}
