using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ContextBar.Core.Search.Tests;

public class ActionSearchEngineTests
{
    [Fact]
    public async Task SearchAsync_IncompatibleActions_NoUsagePoints()
    {
        var registry = new ActionRegistry();

        var incompatible = new MockAction("incompatible.action", "Incompatible", Array.Empty<string>(), ContentKind.None) { IsAvailable = false };
        var compatible = new MockAction("compatible.action", "Compatible", Array.Empty<string>(), ContentKind.Files);

        registry.Register(incompatible);
        registry.Register(compatible);

        var pinned = new HashSet<string> { "incompatible.action" };
        var recent = new Dictionary<string, int> { { "incompatible.action", 40 } };

        var engine = new ActionSearchEngine(registry, null, pinnedActions: pinned, recentUsageScores: recent);
        var context = CreateSnapshot(ContentKind.Files);

        var results = await engine.SearchAsync("", context);

        Assert.Equal(2, results.Count);

        // Compatible should be first because it is compatible (+1000 points).
        // Incompatible should have 0 rank.
        var comp = results.First(r => r.Action.Descriptor.Id == "compatible.action");
        var incomp = results.First(r => r.Action.Descriptor.Id == "incompatible.action");

        Assert.True(comp.Rank > incomp.Rank);
        Assert.Equal(0, incomp.Rank); // Explicit numerical score assertion
        Assert.Equal(1150, comp.Rank); // 1000 + 150(context specificity)
        Assert.Equal(ActionHighlight.None, incomp.Highlight);
    }

    private class MockAction : IAction
    {
        public ActionDescriptor Descriptor { get; }
        public bool IsAvailable { get; set; } = true;
        public bool MatchAsyncCalled { get; private set; }
        public bool ThrowOnMatch { get; set; }

        public MockAction(string id, string title, string[] aliases, params ContentKind[] accepts)
        {
            Descriptor = new ActionDescriptor(
                Id: id,
                Title: title,
                Description: "A mock action",
                Accepts: new HashSet<ContentKind>(accepts),
                SearchTerms: aliases,
                RequiresPreview: false,
                ChangesFiles: false, ExplicitWeight: 0);
        }

        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
        {
            MatchAsyncCalled = true;
            if (ThrowOnMatch) throw new InvalidOperationException("Sensitive source content");
            return new ValueTask<ActionAvailability>(
                new ActionAvailability(IsAvailable, IsAvailable ? null : "Mock unavailable"));
        }

        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
        {
            return new ValueTask<ActionPreview>(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));
        }

        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false));
        }
    }

    private ContextSnapshot CreateSnapshot(ContentKind kind, IReadOnlySet<string>? providedCapabilities = null)
    {
        ContextContent content = kind switch
        {
            ContentKind.Table => new TableData(Array.Empty<IReadOnlyList<CellValue>>(), null, null),
            ContentKind.Files => new FileSelection(Array.Empty<SelectedFile>()),
            ContentKind.Text => new TextSelection("test"),
            _ => new NoContent()
        };

        return new ContextSnapshot(
            InvocationId: Guid.NewGuid(),
            Source: new SourceWindowIdentity(0, 0, null, DateTimeOffset.UtcNow),
            Status: ContextStatus.Available,
            Content: content,
            CapturedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            ProviderId: "MockProvider",
            UserMessage: null,
            ProvidedCapabilities: providedCapabilities);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsAllSortedByCompatibilityAndTitle()
    {
        var registry = new ActionRegistry();

        // Add in mixed order
        registry.Register(new MockAction("z.action", "Zebra", Array.Empty<string>(), ContentKind.None));
        registry.Register(new MockAction("a.action", "Apple", Array.Empty<string>(), ContentKind.Files));
        registry.Register(new MockAction("b.action", "Banana", Array.Empty<string>(), ContentKind.None));

        var engine = new ActionSearchEngine(registry);
        var context = CreateSnapshot(ContentKind.None); // 'Apple' requires Files, so it will be incompatible

        var results = await engine.SearchAsync("", context);

        Assert.Equal(3, results.Count);

        // Banana and Zebra should be compatible, Apple incompatible
        // Banana comes before Zebra because of stable alphabetical sort on Title
        Assert.Equal("Banana", results[0].Action.Descriptor.Title);
        Assert.True(results[0].IsCompatible);

        Assert.Equal("Zebra", results[1].Action.Descriptor.Title);
        Assert.True(results[1].IsCompatible);

        Assert.Equal("Apple", results[2].Action.Descriptor.Title);
        Assert.False(results[2].IsCompatible);
    }

    [Fact]
    public async Task SearchAsync_WithQuery_MatchesAliases()
    {
        var registry = new ActionRegistry();
        registry.Register(new MockAction("img.webp", "Convert to WebP", new[] { "image format", "compress" }, ContentKind.None));
        registry.Register(new MockAction("img.png", "Convert to PNG", Array.Empty<string>(), ContentKind.None));

        var engine = new ActionSearchEngine(registry);
        var context = CreateSnapshot(ContentKind.None);

        var results = await engine.SearchAsync("compress", context);

        // Should find WebP because of the alias
        Assert.Single(results);
        Assert.Equal("img.webp", results[0].Action.Descriptor.Id);
    }

    [Fact]
    public async Task SearchAsync_RanksCompatibleAboveIncompatible()
    {
        var registry = new ActionRegistry();

        // Both match the query 'export'
        registry.Register(new MockAction("export.table", "Export table", new[] { "export" }, ContentKind.Table));
        registry.Register(new MockAction("export.file", "Export file", new[] { "export" }, ContentKind.Files));

        var engine = new ActionSearchEngine(registry);

        // Context is Files
        var context = CreateSnapshot(ContentKind.Files);

        var results = await engine.SearchAsync("export", context);

        Assert.Equal(2, results.Count);

        // Export file should be first because it is compatible with ContentKind.Files
        Assert.Equal("export.file", results[0].Action.Descriptor.Id);
        Assert.True(results[0].IsCompatible);
        Assert.True(results[0].Rank >= 1000);

        // Export table should be second (incompatible)
        Assert.Equal("export.table", results[1].Action.Descriptor.Id);
        Assert.False(results[1].IsCompatible);
        Assert.True(results[1].Rank < 1000); // No compatibility bonus
    }

    [Fact]
    public async Task SearchAsync_FuzzyMatch_FindsMisspellings()
    {
        var registry = new ActionRegistry();
        registry.Register(new MockAction("export.json", "Export table as JSON", Array.Empty<string>(), ContentKind.Table));

        var engine = new ActionSearchEngine(registry);
        var context = CreateSnapshot(ContentKind.Table);

        // Typo: xport instead of export
        var results = await engine.SearchAsync("xport json", context);

        Assert.Single(results);
        Assert.Equal("export.json", results[0].Action.Descriptor.Id);
    }

    [Fact]
    public async Task SearchAsync_WithInjectedAliases_MatchesAndRanksAliases()
    {
        var registry = new ActionRegistry();
        registry.Register(new MockAction("img.webp", "Convert to WebP", Array.Empty<string>(), ContentKind.None));
        registry.Register(new MockAction("img.png", "Convert to PNG", Array.Empty<string>(), ContentKind.None));

        var aliases = new Dictionary<string, IReadOnlyList<string>>
        {
            { "img.webp", new List<string> { "bild", "konvertieren" } }
        };

        var engine = new ActionSearchEngine(registry, aliases);
        var context = CreateSnapshot(ContentKind.None);

        // Search for "konvertieren" which is a German alias for webp
        var results = await engine.SearchAsync("konvertieren", context);

        Assert.Single(results);
        Assert.Equal("img.webp", results[0].Action.Descriptor.Id);
    }

    [Fact]
    public async Task SearchAsync_WithInjectedAliases_HandlesFuzzyMatchesOnAliases()
    {
        var registry = new ActionRegistry();
        registry.Register(new MockAction("resize", "Resize Image", Array.Empty<string>(), ContentKind.None));

        var aliases = new Dictionary<string, IReadOnlyList<string>>
        {
            { "resize", new List<string> { "verkleinern" } }
        };

        var engine = new ActionSearchEngine(registry, aliases);
        var context = CreateSnapshot(ContentKind.None);

        // Fuzzy match: vkleinern instead of verkleinern
        var results = await engine.SearchAsync("verkleinrn", context);

        Assert.Single(results);
        Assert.Equal("resize", results[0].Action.Descriptor.Id);
    }

    [Fact]
    public async Task SearchAsync_MaintainsStableOrderingOnTies()
    {
        var registry = new ActionRegistry();

        // Same exact title, should sort by ID
        registry.Register(new MockAction("id2", "Same Title", Array.Empty<string>(), ContentKind.None));
        registry.Register(new MockAction("id1", "Same Title", Array.Empty<string>(), ContentKind.None));
        registry.Register(new MockAction("id3", "Same Title", Array.Empty<string>(), ContentKind.None));

        var engine = new ActionSearchEngine(registry);
        var context = CreateSnapshot(ContentKind.None);

        var results = await engine.SearchAsync("", context);

        Assert.Equal(3, results.Count);
        Assert.Equal("id1", results[0].Action.Descriptor.Id);
        Assert.Equal("id2", results[1].Action.Descriptor.Id);
        Assert.Equal("id3", results[2].Action.Descriptor.Id);
    }
    [Fact]
    public async Task SearchAsync_CapabilityMissing_StillMatchesGermanAliasButIsUnavailable()
    {
        var registry = new ActionRegistry();
        var action = new MockAction("excel.format", "Format table", Array.Empty<string>(), ContentKind.Table);
        registry.Register(new CapabilityAction(action, "table.style.preserve.v1"));
        var aliases = new Dictionary<string, IReadOnlyList<string>> {
            ["excel.format"] = new[] { "formatieren" }
        };

        var engine = new ActionSearchEngine(registry, aliases);
        var context = CreateSnapshot(ContentKind.Table);
        var results = await engine.SearchAsync("formatieren", context);

        Assert.Single(results);
        Assert.False(results[0].IsCompatible);
        Assert.Equal(ContextBar.Abstractions.Resources.SdkStrings.MissingRequiredCapabilities, results[0].IncompatibilityReason);
        Assert.False(action.MatchAsyncCalled);
    }

    [Fact]
    public async Task SearchAsync_ExactVersionedCapability_MatchesOnAnyProvider()
    {
        var registry = new ActionRegistry();
        var action = new MockAction("excel.format", "Format table", Array.Empty<string>(), ContentKind.Table);
        registry.Register(new CapabilityAction(action, "table.style.preserve.v1"));
        var engine = new ActionSearchEngine(registry);

        var available = await engine.SearchAsync("", CreateSnapshot(ContentKind.Table,
            new HashSet<string> { "table.style.preserve.v1", "unrelated.v1" }));
        Assert.True(Assert.Single(available).IsCompatible);

        var wrongVersion = await engine.SearchAsync("", CreateSnapshot(ContentKind.Table,
            new HashSet<string> { "table.style.preserve.v2" }));
        Assert.False(Assert.Single(wrongVersion).IsCompatible);
    }

    [Fact]
    public async Task SearchAsync_ThrowingMatcher_DoesNotPreventOtherActions()
    {
        var registry = new ActionRegistry();
        registry.Register(new MockAction("broken", "Broken", Array.Empty<string>(), ContentKind.None)
            { ThrowOnMatch = true });
        registry.Register(new MockAction("safe", "Safe", Array.Empty<string>(), ContentKind.None));
        var results = await new ActionSearchEngine(registry).SearchAsync("",
            CreateSnapshot(ContentKind.None));

        Assert.Equal(2, results.Count);
        Assert.Equal("safe", results[0].Action.Descriptor.Id);
        Assert.True(results[0].IsCompatible);
        Assert.Equal(ContextBar.Core.Resources.Strings.ActionAvailabilityUnknown, results[1].IncompatibilityReason);
    }

    private sealed class CapabilityAction : IAction
    {
        private readonly MockAction _inner;
        public CapabilityAction(MockAction inner, string required)
        {
            _inner = inner;
            Descriptor = inner.Descriptor with { RequiredCapabilities = new HashSet<string> { required } };
        }
        public ActionDescriptor Descriptor { get; }
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken ct)
            => _inner.MatchAsync(context, ct);
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters args, CancellationToken ct)
            => _inner.PreviewAsync(context, args, ct);
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters args,
            IProgress<ActionProgress>? progress, CancellationToken ct)
            => _inner.ExecuteAsync(context, args, progress, ct);
    }


    [Fact]
    public async Task SearchAsync_ContextSpecificity_PrioritizesCorrectKind()
    {
        var registry = new ActionRegistry();
        // Since we reverted the MockAction signature, let's just make two MockActions that accept multiple ContentKinds. Wait, if an action doesn't accept ContentKind.Files, it will be marked incompatible and put at the bottom. The test is about breaking ties for compatible actions.
        // MockAction accepts what we pass in.
        // We want action.file to accept Files AND None, action.table to accept Table AND None.
        // Wait, if an action accepts ContentKind.None, it is universally compatible.
        registry.Register(new MockAction("action.file", "Export data", new[] { "export" }, ContentKind.Files, ContentKind.None));
        registry.Register(new MockAction("action.table", "Export data", new[] { "export" }, ContentKind.Table, ContentKind.None));

        var engine = new ActionSearchEngine(registry);

        // Context is Files (e.g. Explorer)
        var resultsFiles = await engine.SearchAsync("export", CreateSnapshot(ContentKind.Files));
        Assert.Equal(2, resultsFiles.Count);
        Assert.Equal("action.file", resultsFiles[0].Action.Descriptor.Id); // File specific action gets +150
        Assert.Equal("action.table", resultsFiles[1].Action.Descriptor.Id);

        // Context is Table (e.g. Excel)
        var resultsTable = await engine.SearchAsync("export", CreateSnapshot(ContentKind.Table));
        Assert.Equal(2, resultsTable.Count);
        Assert.Equal("action.table", resultsTable[0].Action.Descriptor.Id); // Table specific action gets +150
        Assert.Equal("action.file", resultsTable[1].Action.Descriptor.Id);
    }

    [Fact]
    public async Task SearchAsync_PinnedAction_BoostsRank()
    {
        var registry = new ActionRegistry();
        registry.Register(new MockAction("action.a", "Action A", Array.Empty<string>(), ContentKind.None));
        registry.Register(new MockAction("action.b", "Action B", Array.Empty<string>(), ContentKind.None));

        // Pin action B
        var pinned = new HashSet<string> { "action.b" };
        var engine = new ActionSearchEngine(registry, null, pinned, null);

        var results = await engine.SearchAsync("", CreateSnapshot(ContentKind.None));

        // B should be first because it's pinned (+100 rank)
        Assert.Equal(2, results.Count);
        Assert.Equal("action.b", results[0].Action.Descriptor.Id);
        Assert.Equal("action.a", results[1].Action.Descriptor.Id);
    }

    // Since we can't easily add ExplicitWeight to MockAction without breaking other tests due to params,
    // we can create a specific MockActionWithWeight class just for this test.
    private class MockActionWithWeight : IAction
    {
        public ActionDescriptor Descriptor { get; }

        public MockActionWithWeight(string id, string title, int explicitWeight)
        {
            Descriptor = new ActionDescriptor(
                Id: id,
                Title: title,
                Description: "A mock action",
                Accepts: new HashSet<ContentKind> { ContentKind.None },
                SearchTerms: Array.Empty<string>(),
                RequiresPreview: false,
                ChangesFiles: false,
                ExplicitWeight: explicitWeight);
        }

        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    [Fact]
    public async Task SearchAsync_ExplicitWeight_Applied()
    {
        var registry = new ActionRegistry();
        registry.Register(new MockActionWithWeight("action.a", "Test Action", explicitWeight: 0));
        registry.Register(new MockActionWithWeight("action.b", "Test Action", explicitWeight: 50));

        var engine = new ActionSearchEngine(registry);

        var results = await engine.SearchAsync("", CreateSnapshot(ContentKind.None));

        // Action B should be first because of explicit weight 50
        Assert.Equal(2, results.Count);
        Assert.Equal("action.b", results[0].Action.Descriptor.Id);
        Assert.Equal("action.a", results[1].Action.Descriptor.Id);
    }

    [Fact]
    public async Task SearchAsync_RecentUsage_BoostsRank()
    {
        var registry = new ActionRegistry();
        registry.Register(new MockAction("action.a", "Action A", Array.Empty<string>(), ContentKind.None));
        registry.Register(new MockAction("action.b", "Action B", Array.Empty<string>(), ContentKind.None));
        registry.Register(new MockAction("action.c", "Action C", Array.Empty<string>(), ContentKind.None));

        // B has usage score 30, C has usage score 100 (capped at 40)
        var recentUsage = new Dictionary<string, int>
        {
            { "action.b", 30 },
            { "action.c", 100 }
        };

        var engine = new ActionSearchEngine(registry, null, null, recentUsage);
        var results = await engine.SearchAsync("", CreateSnapshot(ContentKind.None));

        // C should be first (score 40), then B (score 30), then A (score 0)
        Assert.Equal(3, results.Count);
        Assert.Equal("action.c", results[0].Action.Descriptor.Id);
        Assert.Equal("action.b", results[1].Action.Descriptor.Id);
        Assert.Equal("action.a", results[2].Action.Descriptor.Id);
    }

    [Fact]
    public async Task SearchAsync_AppIdentity_BoostsRank()
    {
        var registry = new ActionRegistry();
        // Both accept Table, but action.excel lists "excel.exe" in search terms
        registry.Register(new MockAction("action.generic", "Export table", Array.Empty<string>(), ContentKind.Table, ContentKind.None));
        registry.Register(new MockAction("action.excel", "Export to Excel Format", new[] { "excel.exe" }, ContentKind.Table, ContentKind.None));

        var engine = new ActionSearchEngine(registry);

        var source = new SourceWindowIdentity(IntPtr.Zero, 1234, "excel.exe", DateTimeOffset.Now, "Book1 - Excel");
        var context = new ContextSnapshot(
            Guid.NewGuid(),
            source,
            ContextStatus.Available,
            new TableData(new List<IReadOnlyList<CellValue>>(), "Excel", "A1:B2"),
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddMinutes(5),
            "FakeProvider",
            null
        );

        var results = await engine.SearchAsync("", context);

        // Action excel should get +125 for app identity
        Assert.Equal(2, results.Count);
        Assert.Equal("action.excel", results[0].Action.Descriptor.Id);
        Assert.Equal("action.generic", results[1].Action.Descriptor.Id);
    }
}
