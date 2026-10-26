using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Application;
using ContextBar.Application.ViewModels;
using ContextBar.Core;
using ContextBar.Core.Search;
using Xunit;

namespace ContextBar.Application.Tests;

public class InvocationCoordinatorTests
{
    [Fact]
    public async Task ExplicitActivationUsesSuppliedSnapshotAndOnlySelectsTheAction()
    {
        var registry = new ActionRegistry();
        var action = new ControlledAction();
        registry.Register(action);
        var engine = new ActionSearchEngine(registry);
        var vm = new PaletteViewModel();
        var coordinator = new InvocationCoordinator(null, new ForbiddenCapture(), new ContextResolver(Array.Empty<IContextProvider>()), vm, registry, engine);
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 0, null, now),
            ContextStatus.Available, new NoContent(), now, now.AddMinutes(1), "explicit-selection", null);
        Assert.True(await coordinator.ActivateSnapshotAsync(snapshot, action.Descriptor.Id));
        Assert.Same(snapshot, vm.CurrentSnapshot);
        Assert.Equal(action.Descriptor.Id, vm.SelectedAction?.Action.Descriptor.Id);
        Assert.False(vm.IsExecuting);
        Assert.False(action.WaitForExecutionStartedAsync().IsCompleted);
    }

    [Fact]
    public async Task GenericActivationClearsPriorSelectionWithoutReadingForeground()
    {
        var registry = new ActionRegistry();
        var engine = new ActionSearchEngine(registry);
        var vm = new PaletteViewModel();
        var coordinator = new InvocationCoordinator(null, new ForbiddenCapture(), new ContextResolver(Array.Empty<IContextProvider>()), vm, registry, engine);
        var now = DateTimeOffset.UtcNow;
        var old = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 0, null, now),
            ContextStatus.Available, new FileSelection([new SelectedFile(@"C:\fixture.pdf", "fixture.pdf", ".pdf", null, 10, true, true, null)]),
            now, now.AddMinutes(1), "explicit-selection", null);
        await coordinator.ActivateSnapshotAsync(old);
        var generic = old with { InvocationId = Guid.NewGuid(), Status = ContextStatus.Empty, Content = new NoContent(), ProviderId = "activation" };
        await coordinator.ActivateSnapshotAsync(generic);
        Assert.IsType<NoContent>(vm.CurrentSnapshot?.Content);
        Assert.False(vm.IsContextAvailable);
        Assert.Empty(vm.SearchText);
    }

    private sealed class ForbiddenCapture : IForegroundWindowCapture
    {
        public ValueTask<ForegroundCaptureResult> CaptureForegroundAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Activation must not inspect the foreground.");
    }

    [Fact]
    public async Task InvokeAsync_ClearsPreviousSearchBeforeRequestingThePalette()
    {
        var registry = new ActionRegistry();
        var engine = new ActionSearchEngine(registry);
        var vm = new PaletteViewModel();
        vm.SearchText = "previous query";
        vm.IncludeUnavailableActions = true;
        var coordinator = new InvocationCoordinator(null, new DummyCapture(), new ContextResolver(Array.Empty<IContextProvider>()), vm, registry, engine);
        bool shown = false;
        coordinator.PaletteRequested += (_, source) =>
        {
            shown = true;
            Assert.True(source.HasValue); // zero here means failed/unknown source, not resurface
            Assert.Empty(vm.SearchText);
            Assert.Empty(vm.FilteredActions);
            Assert.Null(vm.SelectedAction);
            Assert.False(vm.IncludeUnavailableActions);
        };
        await coordinator.InvokeAsync();
        Assert.True(shown);
    }

    private class DelayedProvider : IContextProvider
    {
        public string Id => "Delayed";
        private readonly int _delayMs;
        private readonly bool _throwException;

        public DelayedProvider(int delayMs, bool throwException = false)
        {
            _delayMs = delayMs;
            _throwException = throwException;
        }

        public async ValueTask<bool> CanCaptureAsync(SourceWindowIdentity source, CancellationToken cancellationToken)
        {
            await Task.Delay(_delayMs, cancellationToken);
            if (_throwException) throw new InvalidOperationException("Delayed exception");
            return true;
        }

        public async ValueTask<ContextSnapshot> CaptureAsync(Guid invocationId, SourceWindowIdentity source, CancellationToken cancellationToken)
        {
            await Task.Delay(_delayMs, cancellationToken);
            if (_throwException) throw new InvalidOperationException("Delayed exception");

            return new ContextSnapshot(
                invocationId,
                source,
                ContextStatus.Available,
                new NoContent(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5),
                Id,
                "Success");
        }
    }

    private class DummyCapture : IForegroundWindowCapture
    {
        public ValueTask<ForegroundCaptureResult> CaptureForegroundAsync(CancellationToken cancellationToken = default)
        {
            var identity = new SourceWindowIdentity(IntPtr.Zero, 123, "test", DateTimeOffset.UtcNow);
            return new ValueTask<ForegroundCaptureResult>(new ForegroundCaptureResult(identity, ForegroundCaptureError.None));
        }
    }

    [Fact]
    public async Task InvokeAsync_LatestInvocationWins_SupersedingEarlierOne()
    {
        // Setup
        var provider = new DelayedProvider(100); // 100ms delay
        var resolver = new ContextResolver(new List<IContextProvider> { provider });
        var capture = new DummyCapture();
        var registry = new ActionRegistry();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        var vm = new PaletteViewModel();

        var coordinator = new InvocationCoordinator(null, capture, resolver, vm, registry, searchEngine);

        // Act
        var firstInvocationTask = coordinator.InvokeAsync();
        // Slightly delay before second invocation but before the first one finishes
        await Task.Delay(20);
        var secondInvocationTask = coordinator.InvokeAsync();

        await Task.WhenAll(firstInvocationTask, secondInvocationTask);

        // Assert
        // VM summary should reflect the state from the second successful load.
        // It should NOT contain errors from cancellation, and it should show No context available because we pass a NoContent payload which doesn't match specific types.
        Assert.Equal(ContextBar.Application.Resources.Strings.GetString("Context_UnknownSelection"), vm.ContextSummary);
    }

    [Fact]
    public async Task InvokeAsync_ProviderException_ConvertedToFailedStateInViewModel()
    {
        // Setup
        var provider = new DelayedProvider(10, throwException: true);
        var resolver = new ContextResolver(new List<IContextProvider> { provider });
        var capture = new DummyCapture();
        var registry = new ActionRegistry();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        var vm = new PaletteViewModel();

        var coordinator = new InvocationCoordinator(null, capture, resolver, vm, registry, searchEngine);

        // Act
        await coordinator.InvokeAsync();

        // Assert
        // Raw provider/exception text is never shown; the palette explains the status instead.
        Assert.DoesNotContain("Delayed exception", vm.ContextSummary);
        Assert.DoesNotContain("Delayed exception", vm.ContextNotice ?? string.Empty);
        Assert.False(vm.IsContextAvailable);
        Assert.False(string.IsNullOrEmpty(vm.ContextNotice));
    }



    private class ControlledAction : IAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor("contextbar.test.controlled", "Controlled", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false);
        private readonly TaskCompletionSource _executionStarted = new();
        private readonly TaskCompletionSource _continueExecution = new();

        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) => new(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));

        public async Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            _executionStarted.SetResult();
            using var reg = cancellationToken.Register(() => _continueExecution.TrySetCanceled());
            await _continueExecution.Task;
            return new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false);
        }

        public Task WaitForExecutionStartedAsync() => _executionStarted.Task;
    }

    [Fact]
    public async Task InvokeAsync_WhenExecuting_FiresPaletteRequestedButSkipsResolution()
    {
        var provider = new DelayedProvider(10);
        var resolver = new ContextResolver(new List<IContextProvider> { provider });
        var capture = new DummyCapture();
        var registry = new ActionRegistry();

        var controlledAction = new ControlledAction();
        registry.Register(controlledAction);

        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());

        var aliases = new Dictionary<string, IReadOnlyList<string>>();
        var vm = new PaletteViewModel();

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);
        vm.Initialize(snapshot, registry, searchEngine);
        await vm.SearchTask!;

        var coordinator = new InvocationCoordinator(null, capture, resolver, vm, registry, searchEngine);

        int paletteRequestedCount = 0;
        int resurfaceRequestedCount = 0;
        nint? requestedSource = 123;
        coordinator.PaletteRequested += (s, e) => { paletteRequestedCount++; requestedSource = e; };
        coordinator.ResurfaceRequested += (s, e) => { resurfaceRequestedCount++; };

        vm.SelectedAction = vm.FilteredActions.First(a => a.Action.Descriptor.Id == "contextbar.test.controlled");

        var execTask = vm.ExecuteSelectedActionAsync();

        await controlledAction.WaitForExecutionStartedAsync();
        await Task.Yield();

        Assert.True(vm.IsExecuting);

        await coordinator.InvokeAsync();

        Assert.Equal(0, paletteRequestedCount);
        Assert.Equal(1, resurfaceRequestedCount);

        vm.CancelExecution();
        await execTask;
    }

    [Fact]
    public void Dismiss_WhenIdle_ClearsViewModelState()
    {
        // Setup
        var provider = new DelayedProvider(10);
        var resolver = new ContextResolver(new List<IContextProvider> { provider });
        var capture = new DummyCapture();
        var registry = new ActionRegistry();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        var vm = new PaletteViewModel();
        var coordinator = new InvocationCoordinator(null, capture, resolver, vm, registry, searchEngine);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);
        vm.Initialize(snapshot, registry, searchEngine);
        vm.SearchText = "test query";

        // Act
        coordinator.Dismiss();

        // Assert
        Assert.Equal(ContextBar.Application.Resources.Strings.GetString("Context_UnknownSelection"), vm.ContextSummary);
        Assert.Empty(vm.SearchText);
        Assert.Empty(vm.FilteredActions);
        Assert.Null(vm.SelectedAction);
    }

    [Fact]
    public async Task Dismiss_DuringCapture_PreventsStateRestore()
    {
        // Setup
        var provider = new DelayedProvider(500); // long delay
        var resolver = new ContextResolver(new List<IContextProvider> { provider });
        var capture = new DummyCapture();
        var registry = new ActionRegistry();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        var vm = new PaletteViewModel();
        var coordinator = new InvocationCoordinator(null, capture, resolver, vm, registry, searchEngine);

        // Act
        var invokeTask = coordinator.InvokeAsync();

        // Wait briefly for invoke to start and set loading state
        await Task.Delay(50);

        Assert.Equal(ContextBar.Application.Resources.Strings.GetString("Context_Loading"), vm.ContextSummary);

        coordinator.Dismiss();

        await invokeTask;

        // Assert
        // After dismiss, state should be cleared, and completion of provider should NOT restore it.
        Assert.Equal(ContextBar.Application.Resources.Strings.GetString("Context_UnknownSelection"), vm.ContextSummary);
        Assert.Empty(vm.SearchText);
    }

    [Fact]
    public async Task Dismiss_DuringExecution_PreservesState()
    {
        // Setup
        var provider = new DelayedProvider(10);
        var resolver = new ContextResolver(new List<IContextProvider> { provider });
        var capture = new DummyCapture();
        var registry = new ActionRegistry();
        var controlledAction = new ControlledAction();
        registry.Register(controlledAction);
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        var vm = new PaletteViewModel();

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);
        vm.Initialize(snapshot, registry, searchEngine);
        await vm.SearchTask!;

        var coordinator = new InvocationCoordinator(null, capture, resolver, vm, registry, searchEngine);

        vm.SelectedAction = vm.FilteredActions.First(a => a.Action.Descriptor.Id == "contextbar.test.controlled");

        // Act
        var execTask = vm.ExecuteSelectedActionAsync();
        await controlledAction.WaitForExecutionStartedAsync();

        Assert.True(vm.IsExecuting);
        vm.SearchText = "query"; // Set after executing to avoid filtering out the action

        coordinator.Dismiss();

        // Assert
        // Should not clear state because execution is active.
        Assert.Equal("query", vm.SearchText);
        Assert.NotNull(vm.SelectedAction);
        Assert.True(vm.IsExecuting);

        vm.CancelExecution();
        await execTask;
    }

    [Fact]
    public async Task Invoke_AfterDismiss_WorksWithFreshSnapshot()
    {
        // Setup
        var provider = new DelayedProvider(10);
        var resolver = new ContextResolver(new List<IContextProvider> { provider });
        var capture = new DummyCapture();
        var registry = new ActionRegistry();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        var vm = new PaletteViewModel();
        var coordinator = new InvocationCoordinator(null, capture, resolver, vm, registry, searchEngine);

        var initialSnapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);
        vm.Initialize(initialSnapshot, registry, searchEngine);
        vm.SearchText = "first";

        // Act
        coordinator.Dismiss();

        Assert.Empty(vm.SearchText);
        Assert.Equal(ContextBar.Application.Resources.Strings.GetString("Context_UnknownSelection"), vm.ContextSummary);

        await coordinator.InvokeAsync();

        // Assert
        // Should be replaced with new snapshot/state, not null exception.
        // The mock capture/resolver gives a successful empty content snapshot.
        Assert.Equal(ContextBar.Application.Resources.Strings.GetString("Context_UnknownSelection"), vm.ContextSummary);
    }
}
