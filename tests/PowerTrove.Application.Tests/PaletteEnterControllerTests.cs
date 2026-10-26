using PowerTrove.Application.Search;
using PowerTrove.Application.ViewModels;
using PowerTrove.Core;
using PowerTrove.Core.Search;

namespace PowerTrove.Application.Tests;

public sealed class PaletteEnterControllerTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task EditingQueryOrMovingSelection_RetiresResult_AndEnterRunsTheNewSelection(bool outputs, bool editQuery)
    {
        var first = new CountingAction("first", outputs); var next = new CountingAction("next", false);
        var engine = new ScriptedEngine(first, next);
        var vm = Create(engine, first, next); await vm.SearchTask;
        await vm.ExecuteSelectedActionAsync();
        Assert.True(vm.HasExecutionResult); Assert.Equal(1, first.Executions);
        if (editQuery)
        {
            engine.Results = new[] { Result(next) };
            vm.SearchText = "new intent";
            Assert.False(vm.HasExecutionResult);
            await vm.SearchTask;
        }
        else vm.MoveSelectionDown();
        Assert.False(vm.HasExecutionResult); Assert.Empty(vm.CurrentOutputPaths);
        var controller = new PaletteEnterController();
        Assert.Equal(PaletteEnterOutcome.Executed, await controller.HandleEnterAsync(vm, _ => vm.ExecuteSelectedActionAsync(), () => true));
        Assert.Equal(1, next.Executions); Assert.Equal(1, first.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedEnterDuringSearchRetry_NeverExecutes_AndRetryButtonHasNoExecutePath(bool buttonStartsRetry)
    {
        var action = new CountingAction("one", false); var engine = new ScriptedEngine(action) { Fail = true };
        var vm = Create(engine, action); await vm.SearchTask; Assert.NotNull(vm.SearchError);
        var pending = new TaskCompletionSource<IReadOnlyList<ActionSearchResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Fail = false; engine.Pending = pending.Task;
        var controller = new PaletteEnterController();
        Task retry = buttonStartsRetry ? controller.RetryAsync(vm, () => true)
            : controller.HandleEnterAsync(vm, _ => vm.ExecuteSelectedActionAsync(), () => true);
        Assert.True(controller.IsPending);
        Assert.Equal(PaletteEnterOutcome.Ignored, await controller.HandleEnterAsync(vm, _ => vm.ExecuteSelectedActionAsync(), () => true));
        pending.SetResult(new[] { Result(action) }); await retry;
        Assert.False(controller.IsPending); Assert.Null(vm.SearchError); Assert.Equal(0, action.Executions);
        engine.Pending = null;
        Assert.Equal(PaletteEnterOutcome.Executed, await controller.HandleEnterAsync(vm, _ => vm.ExecuteSelectedActionAsync(), () => true));
        Assert.Equal(1, action.Executions);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("selection")]
    [InlineData("dismiss")]
    [InlineData("search")]
    public async Task PendingEnter_DoesNotExecuteAfterItsCapturedIntentChanges(string change)
    {
        var first = new CountingAction("first", false); var next = new CountingAction("next", false);
        var engine = new ScriptedEngine(first, next); var vm = Create(engine, first, next); await vm.SearchTask;
        var pending = new TaskCompletionSource<IReadOnlyList<ActionSearchResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Pending = pending.Task; vm.SearchText = "pending";
        var controller = new PaletteEnterController(); bool visible = true;
        var enter = controller.HandleEnterAsync(vm, _ => vm.ExecuteSelectedActionAsync(), () => visible);
        if (change == "query") vm.SearchText = "changed";
        if (change == "selection") vm.MoveSelectionDown();
        if (change == "dismiss") { visible = false; controller.Invalidate(); }
        if (change == "search") _ = vm.RetrySearchAsync();
        pending.SetResult(new[] { Result(first), Result(next) });
        Assert.Equal(PaletteEnterOutcome.Ignored, await enter);
        await vm.SearchTask;
        Assert.Equal(0, first.Executions + next.Executions);
    }

    [Fact]
    public async Task EnterWhileResultPanelRemainsVisible_DoesNotRunAgain()
    {
        var action = new CountingAction("one", true); var engine = new ScriptedEngine(action);
        var vm = Create(engine, action); await vm.SearchTask; await vm.ExecuteSelectedActionAsync();
        Assert.Equal(PaletteEnterOutcome.Ignored, await new PaletteEnterController().HandleEnterAsync(vm, _ => vm.ExecuteSelectedActionAsync(), () => true));
        Assert.Equal(1, action.Executions);
    }

    private static PaletteViewModel Create(ScriptedEngine engine, params CountingAction[] actions)
    {
        var registry = new ActionRegistry(); foreach (var action in actions) registry.Register(action);
        var vm = new PaletteViewModel(); vm.Initialize(new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(0, 0, null, DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "test", null), registry, engine); return vm;
    }
    private static ActionSearchResult Result(CountingAction action) => new(action, true, null, 100);
    private sealed class ScriptedEngine(params CountingAction[] actions) : IActionSearchEngine
    {
        public IReadOnlyList<ActionSearchResult> Results { get; set; } = actions.Select(Result).ToArray();
        public bool Fail { get; set; }
        public Task<IReadOnlyList<ActionSearchResult>>? Pending { get; set; }
        public void RefreshSignals() { }
        public Task<IReadOnlyList<ActionSearchResult>> SearchAsync(string query, ContextSnapshot context, CancellationToken ct = default) =>
            Fail ? Task.FromException<IReadOnlyList<ActionSearchResult>>(new InvalidOperationException("test")) : Pending ?? Task.FromResult(Results);
    }
    private sealed class CountingAction(string id, bool outputs) : IAction
    {
        public int Executions { get; private set; }
        public ActionDescriptor Descriptor { get; } = new("powertrove.test." + id, id, "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, outputs);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken ct) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken ct)
        {
            Executions++;
            return Task.FromResult(new ActionReceipt(true, false, outputs ? new[] { "synthetic-output.pdf" } : Array.Empty<string>(), Array.Empty<ActionFailure>(), false));
        }
    }
}
