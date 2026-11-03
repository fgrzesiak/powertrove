using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Core;
using PowerTrove.Desktop;
using Xunit;

namespace PowerTrove.Desktop.Tests;

public class BrowserCompositionTests
{
    private sealed class NoPages : IBrowserPageService
    {
        public Task<BrowserPageContent?> FetchAsync(BrowserPageInfo page, BrowserPageOperation operation,
            CancellationToken cancellationToken) => Task.FromResult<BrowserPageContent?>(null);
    }

    private static ContextSnapshot BrowserTab()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new SourceWindowIdentity(new IntPtr(1), 1, "chrome", now, "Example - Google Chrome");
        return new ContextSnapshot(Guid.NewGuid(), source, ContextStatus.Available,
            new TextSelection("https://example.org/article"), now, now.AddMinutes(2),
            "powertrove.provider.browser.explicit", "https://example.org/article",
            new HashSet<string> { "browser.selection.read.v1" })
        {
            BrowserPage = new BrowserPageInfo("https://example.org/article", "Example", "example.org",
                false, 0, true, 0, 0, 0, 0, 0, "en", 800)
            {
                Tab = new BrowserTabHandle(Guid.NewGuid(), 1, 2, "document-1")
            }
        };
    }

    private static async Task<bool> PageActionMatches(IBrowserPageService? pages)
    {
        var runner = new PowerTrove.Core.Workflows.WorkflowRunner(new ActionRegistry());
        var action = BuiltInActionPacks.Create(runner, browserPages: pages)
            .SelectMany(pack => pack.Actions)
            .Single(a => a.Descriptor.Id == "powertrove.browser.article-markdown");
        return (await action.MatchAsync(BrowserTab(), CancellationToken.None)).IsAvailable;
    }

    [Fact]
    public async Task CompiledInBrowserPack_OffersPageActions_WhenTheAppPassesItsPageService()
    {
        // Without the service the dev build only offered title/URL actions on a connected tab.
        Assert.False(await PageActionMatches(null));
        Assert.True(await PageActionMatches(new NoPages()));
    }
}
