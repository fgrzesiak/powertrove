using System.Globalization;
using PowerTrove.Application.ViewModels;
using PowerTrove.Core;
using PowerTrove.Core.Search;

namespace PowerTrove.Application.Tests;

public sealed class OutputRevealFeedbackTests
{
    [Fact]
    public async Task RevealFailure_IsVisibleWithoutRawDetails_AndDoesNotChangeExecutionSuccess()
    {
        var vm = Create(); await vm.SearchTask; await vm.ExecuteSelectedActionAsync();
        var outputs = vm.CurrentOutputPaths; string? message = vm.ExecutionResultMessage;
        vm.OutputRevealer = new FakeRevealer { Reveal = () => Task.FromException(new InvalidOperationException("private-native-marker")) };
        await vm.RevealOutputsAsync();
        Assert.NotNull(vm.OutputNavigationMessage); Assert.DoesNotContain("private", vm.OutputNavigationMessage!);
        Assert.True(vm.ExecutionIsSuccess); Assert.Equal(message, vm.ExecutionResultMessage);
        Assert.Same(outputs, vm.CurrentOutputPaths); Assert.Equal(1, vm.OutputCount);
        vm.OutputRevealer = new FakeRevealer();
        await vm.RevealOutputsAsync(); Assert.Null(vm.OutputNavigationMessage);
    }

    [Fact]
    public async Task LateRevealFailure_CannotRestoreAnOldNotice_AfterBrowsingOrAnotherExecution()
    {
        var vm = Create(); await vm.SearchTask; await vm.ExecuteSelectedActionAsync();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.OutputRevealer = new FakeRevealer { Reveal = () => pending.Task };
        var reveal = vm.RevealOutputsAsync();
        vm.SearchText = "changed"; await vm.SearchTask;
        Assert.False(vm.HasExecutionResult);
        await vm.ExecuteSelectedActionAsync();
        pending.SetException(new InvalidOperationException("private-marker")); await reveal;
        Assert.True(vm.HasExecutionResult); Assert.Null(vm.OutputNavigationMessage);
    }

    [Fact]
    public async Task Notice_IsLocalized_AndRetiredWithTheResultPanel()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            var vm = Create(); await vm.SearchTask; await vm.ExecuteSelectedActionAsync();
            vm.OutputRevealer = new FakeRevealer { Reveal = () => Task.FromException(new OutputRevealException()) };
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            await vm.RevealOutputsAsync(); string? english = vm.OutputNavigationMessage;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            await vm.RevealOutputsAsync();
            Assert.NotEqual(english, vm.OutputNavigationMessage); Assert.Contains("Versuchen Sie", vm.OutputNavigationMessage!);
            vm.SearchText = "new intent"; Assert.False(vm.HasExecutionResult); Assert.Null(vm.OutputNavigationMessage);
            await vm.SearchTask;
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    private static PaletteViewModel Create()
    {
        var action = new OutputAction(); var registry = new ActionRegistry(); registry.Register(action);
        var vm = new PaletteViewModel(); vm.Initialize(new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(0, 0, null, DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "test", null), registry,
            new Engine(action)); return vm;
    }
    private sealed class FakeRevealer : IOutputRevealer
    {
        public Func<Task> Reveal { get; init; } = () => Task.CompletedTask;
        public Task RevealOutputsAsync(IReadOnlyList<string> paths) => Reveal();
        public Task OpenSingleOutputAsync(string path) => Task.CompletedTask;
    }
    private sealed class Engine(IAction action) : IActionSearchEngine
    {
        public void RefreshSignals() { }
        public Task<IReadOnlyList<ActionSearchResult>> SearchAsync(string query, ContextSnapshot context, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActionSearchResult>>(new[] { new ActionSearchResult(action, true, null, 100) });
    }
    private sealed class OutputAction : IAction
    {
        private readonly string[] _sameOutputs = { "synthetic-output.pdf" };
        public ActionDescriptor Descriptor { get; } = new("powertrove.test.output", "Output", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, true);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken ct) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken ct) =>
            Task.FromResult(new ActionReceipt(true, false, _sameOutputs, Array.Empty<ActionFailure>(), false, "Completed"));
    }
}
