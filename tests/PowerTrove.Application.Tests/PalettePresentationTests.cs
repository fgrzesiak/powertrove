using PowerTrove.Application.ViewModels;
using PowerTrove.Core;
using PowerTrove.Core.Search;

namespace PowerTrove.Application.Tests;

public class PalettePresentationTests
{
    [Fact]
    public async Task RelatedActionsAreContiguousAndNavigationUsesExactlyTheDisplayedOrder()
    {
        var results = new[] { Result("image.convert-webp"), Result("image.rotate-right"), Result("image.convert-jpeg"), Result("image.flip-horizontal"), Result("files.zip"), Result("unknown.command") };
        var vm = await Create(results);
        Assert.Equal(new[] { "Images · Convert", "Images · Rotate and mirror", "Files · Archives", "All actions" }, vm.Groups.Select(g => g.Title));
        Assert.Equal(new[] { results[0], results[2], results[1], results[3], results[4], results[5] }, vm.FilteredActions);
        for (int i = 1; i <= vm.FilteredActions.Count; i++)
        {
            vm.MoveSelectionDown();
            Assert.Same(vm.FilteredActions[i % vm.FilteredActions.Count], vm.SelectedAction);
        }
        vm.MoveSelectionUp();
        Assert.Same(vm.FilteredActions[^1], vm.SelectedAction);
    }

    [Fact]
    public async Task SuggestionsAreBoundedUniqueAndUnavailableActionsAreOptIn()
    {
        var results = new[] { Result("image.resize", ActionHighlight.LastUsedForSelection), Result("image.compress", ActionHighlight.Favorite), Result("image.convert-webp", ActionHighlight.BestFit), Result("image.convert-jpeg"), Result("pdf.merge", ActionHighlight.Favorite, false) };
        var vm = await Create(results);
        Assert.Equal(3, vm.Groups[0].Items.Count);
        Assert.Equal("Suggested for this selection", vm.Groups[0].Title);
        Assert.Equal(4, vm.FilteredActions.Count);
        Assert.All(vm.FilteredActions, r => Assert.True(r.IsCompatible));
        Assert.True(vm.HasUnavailableActions);
        Assert.Equal(1, vm.UnavailableActionCount);
        Assert.Equal("Show 1 unavailable action", vm.ShowUnavailableLabel);
        var selected = vm.SelectedAction;
        vm.IncludeUnavailableActions = true;
        Assert.Same(selected, vm.SelectedAction);
        Assert.Equal("Unavailable for this selection", vm.Groups[^1].Title);
        Assert.False(vm.FilteredActions[^1].IsCompatible);
        Assert.Equal(5, vm.FilteredActions.Select(r => r.Action.Descriptor.Id).Distinct().Count());
        vm.SelectedAction = vm.FilteredActions[^1];
        vm.IncludeUnavailableActions = false;
        Assert.Same(vm.FilteredActions[0], vm.SelectedAction);
    }

    [Fact]
    public async Task EveryResultNotificationExposesAnAtomicSnapshotAndTheFirstResult()
    {
        var engine = new ScriptedEngine(Result("image.resize"), Result("image.rotate-right"));
        var vm = await Create(engine.Results, engine);
        vm.SelectedAction = vm.FilteredActions[^1];
        int observed = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PaletteViewModel.ResultsRevision) or nameof(PaletteViewModel.Groups) or nameof(PaletteViewModel.FilteredActions))
            {
                observed++;
                Assert.Equal(vm.Groups.SelectMany(g => g.Items), vm.FilteredActions);
                Assert.Same(vm.FilteredActions.FirstOrDefault(), vm.SelectedAction);
            }
        };
        vm.SearchText = "resize";
        await vm.SearchTask;
        Assert.True(observed >= 3);
        Assert.Single(vm.Groups);
        Assert.Equal(string.Empty, vm.Groups[0].Title);
        vm.Clear();
        Assert.Empty(vm.Groups);
        Assert.Null(vm.SelectedAction);
    }

    [Fact]
    public async Task ExplicitNavigationInvalidatesAnEnterSelectionButResultPublicationDoesNot()
    {
        var vm = await Create(new[] { Result("image.resize"), Result("image.rotate-right") });
        long captured = vm.SelectionRevision;
        vm.SearchText = "updated query";
        await vm.SearchTask;
        Assert.Equal(captured, vm.SelectionRevision);
        vm.MoveSelectionDown();
        Assert.True(vm.SelectionRevision > captured);
        captured = vm.SelectionRevision;
        vm.IncludeUnavailableActions = true;
        Assert.True(vm.SelectionRevision > captured);
    }

    [Fact]
    public async Task NewInvocationResetsQuerySelectionAndVisibilityButResolutionKeepsTypedQuery()
    {
        var results = new[] { Result("image.resize"), Result("image.rotate-right") };
        var engine = new ScriptedEngine(results);
        var vm = await Create(results, engine);
        vm.SearchText = "rotation";
        await vm.SearchTask;
        vm.SelectedAction = vm.FilteredActions[^1];
        vm.IncludeUnavailableActions = true;
        var registry = new ActionRegistry();
        var loading = Snapshot() with { Status = ContextStatus.Empty };
        vm.Initialize(loading, registry, engine);
        await vm.SearchTask;
        Assert.Empty(vm.SearchText);
        Assert.False(vm.IncludeUnavailableActions);
        Assert.Same(vm.FilteredActions[0], vm.SelectedAction);
        vm.SearchText = "typed while detecting";
        await vm.SearchTask;
        vm.Initialize(loading with { Status = ContextStatus.Available }, registry, engine);
        await vm.SearchTask;
        Assert.Equal("typed while detecting", vm.SearchText);
        Assert.Equal(vm.SearchText, engine.LastQuery);
    }

    [Fact]
    public async Task AllUnavailableResultsExplainEmptyStateAndRemainDiscoverable()
    {
        var vm = await Create(new[] { Result("pdf.merge", available: false), Result("pdf.split", available: false) });
        Assert.Empty(vm.FilteredActions);
        Assert.True(vm.HasNoResults);
        Assert.Equal("Show 2 unavailable actions", vm.ShowUnavailableLabel);
        vm.IncludeUnavailableActions = true;
        Assert.False(vm.HasNoResults);
        Assert.Equal(2, vm.FilteredActions.Count);
        vm.Clear();
        Assert.False(vm.HasNoResults);
        Assert.False(vm.HasUnavailableActions);
    }

    [Fact]
    public async Task EditingQueryOrMovingSelectionRetiresThePreviousResult()
    {
        var first = Result("image.resize"); var second = Result("image.rotate-right");
        var registry = new ActionRegistry(); registry.Register(first.Action); registry.Register(second.Action);
        var vm = new PaletteViewModel(); vm.Initialize(Snapshot(), registry, new ScriptedEngine(first, second));
        await vm.SearchTask;
        await vm.ExecuteSelectedActionAsync();
        Assert.True(vm.HasExecutionResult);
        vm.MoveSelectionDown();
        Assert.False(vm.HasExecutionResult);
        Assert.Empty(vm.CurrentOutputPaths);
        await vm.ExecuteSelectedActionAsync();
        Assert.True(vm.HasExecutionResult);
        vm.SearchText = "new intent";
        await vm.SearchTask;
        Assert.False(vm.HasExecutionResult);
        Assert.Equal(0, vm.OutputCount);
    }

    private static async Task<PaletteViewModel> Create(ActionSearchResult[] results, ScriptedEngine? engine = null)
    {
        var vm = new PaletteViewModel(); vm.Initialize(Snapshot(), new ActionRegistry(), engine ?? new ScriptedEngine(results));
        await vm.SearchTask; return vm;
    }
    private static ContextSnapshot Snapshot() => new(Guid.NewGuid(), new SourceWindowIdentity(1, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "test", null);
    private static ActionSearchResult Result(string id, ActionHighlight highlight = ActionHighlight.None, bool available = true) => new(new TestAction("contextbar." + id), available, available ? null : "Unavailable", 100, highlight);
    private sealed class ScriptedEngine(params ActionSearchResult[] results) : IActionSearchEngine
    {
        public ActionSearchResult[] Results { get; } = results;
        public string? LastQuery { get; private set; }
        public void RefreshSignals() { }
        public Task<IReadOnlyList<ActionSearchResult>> SearchAsync(string query, ContextSnapshot context, CancellationToken ct = default) { LastQuery = query; return Task.FromResult<IReadOnlyList<ActionSearchResult>>(Results); }
    }
    private sealed class TestAction(string id) : IAction
    {
        public ActionDescriptor Descriptor { get; } = new(id, id, "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken ct) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken ct) => Task.FromResult(new ActionReceipt(true, false, new[] { "synthetic-output.txt" }, Array.Empty<ActionFailure>(), false));
    }
}
