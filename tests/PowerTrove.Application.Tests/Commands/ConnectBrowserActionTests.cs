using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions;
using PowerTrove.Application.Commands;
using PowerTrove.Core;
using PowerTrove.Core.Search;
using Xunit;

namespace PowerTrove.Application.Tests.Commands;

public class ConnectBrowserActionTests
{
    private static ContextSnapshot Snapshot(string process) => new(
        Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, process, DateTimeOffset.UtcNow, "Page - Google Chrome"),
        ContextStatus.Unsupported, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

    private static ConnectBrowserAction Action(bool needed, Action? open = null) => new(
        "Browser verbinden", "Erweiterung installieren und verbinden.", "browser chrome edge erweiterung verbinden",
        (_, _) => ValueTask.FromResult(needed), open ?? (() => { }));

    [Fact]
    public async Task WhileTheSetupIsMissingItIsTheFirstEntryOfTheList()
    {
        var registry = new ActionRegistry();
        registry.Register(new OpenSettingsAction(() => { }));
        registry.Register(Action(needed: true));
        var engine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());

        var results = await engine.SearchAsync("", Snapshot("chrome"), CancellationToken.None);

        Assert.Equal(ConnectBrowserAction.ActionId, results[0].Action.Descriptor.Id);
        Assert.True(results[0].IsCompatible);
    }

    [Theory]
    [InlineData("verbinden")]
    [InlineData("erweiterung")]
    [InlineData("extension")]
    public async Task LocalizedAndEnglishWordsFindIt(string query)
    {
        var registry = new ActionRegistry();
        registry.Register(Action(needed: true));
        var engine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());

        var result = Assert.Single(await engine.SearchAsync(query, Snapshot("msedge"), CancellationToken.None));

        Assert.True(result.IsCompatible);
    }

    [Fact]
    public async Task OnceConnectedItIsNotAvailable()
    {
        var availability = await Action(needed: false).MatchAsync(Snapshot("chrome"), CancellationToken.None);
        Assert.False(availability.IsAvailable);
    }

    [Fact]
    public async Task ExecuteOpensTheBrowserSettingsOnce()
    {
        int opened = 0;
        var action = Action(needed: true, () => opened++);

        var receipt = await action.ExecuteAsync(Snapshot("chrome"), new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Equal(1, opened);
        Assert.False(action.Descriptor.ChangesFiles);
        Assert.Equal(action.Descriptor.SearchTerms.Count, action.Descriptor.SearchTerms.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
