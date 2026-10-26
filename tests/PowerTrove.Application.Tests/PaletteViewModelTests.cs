using PowerTrove.Application.Configuration;
using PowerTrove.Core.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Application.Services;
using PowerTrove.Core.Search;
using PowerTrove.Application.ViewModels;
using PowerTrove.Core;
using Xunit;

namespace PowerTrove.Application.Tests;

public class PaletteViewModelTests
{
    [Fact]
    public async Task ClearSelectionUsage_RefreshesSignalsAndRemovesHighlight()
    {
        var action = new TestAction(new ActionDescriptor("test.action", "Test Action", "", new HashSet<ContentKind> { ContentKind.Files }, Array.Empty<string>(), false, false));
        var registry = new ActionRegistry();
        registry.Register(action);

        var settingsService = new MockSettingsService();
        settingsService.Settings = settingsService.Settings with
        {
            SelectionUsage = new Dictionary<string, Dictionary<string, SelectionUsageEntry>>
            {
                { "files:pdf:one", new Dictionary<string, SelectionUsageEntry> { { "test.action", new SelectionUsageEntry(10, DateTimeOffset.UtcNow) } } }
            }
        };

        var signals = new PowerTrove.Application.Search.SelectionUsageSignals(settingsService);
        var searchEngine = new ActionSearchEngine(registry, signals);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new FileSelection(new[] { new SelectedFile("f.pdf", "f.pdf", ".pdf", null, 10, true, true, null) }), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var vm = new PaletteViewModel();
        vm.Initialize(snapshot, registry, searchEngine, settingsService);
        await vm.SearchTask!;

        // The exact match gets best fit, but LastUsedActionId overrides the best fit when there's usage. Actually let's check what it returns
        // The file is a PDF so it's files:pdf:one, the action gets ActionHighlight.LastUsedForSelection

        Assert.True(vm.Groups.Count > 0);
        Assert.Contains(vm.Groups[0].Items, r => r.Highlight == ActionHighlight.LastUsedForSelection);

        // Simulate clear by setting an empty dictionary
        settingsService.Settings = settingsService.Settings with { SelectionUsage = new Dictionary<string, Dictionary<string, SelectionUsageEntry>>() };

        // Refresh search engine signals
        searchEngine.RefreshSignals();

        // Re-trigger search
        vm.SearchText = "t";
        await vm.SearchTask;
        vm.SearchText = "";
        await vm.SearchTask;

        // Verify highlight is gone
        if (vm.Groups.Count > 0)
        {
            Assert.DoesNotContain(vm.Groups[0].Items, r => r.Highlight == ActionHighlight.LastUsedForSelection);
        }
    }

    [Fact]
    public async Task Initialize_SetsContextSummaryAndAvailableActions()
    {
        // Arrange
        var snapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.Now, "Test Window"),
            ContextStatus.Available,
            new FileSelection(new[] { new SelectedFile("test.txt", "test.txt", ".txt", null, null, true, true, null, false) }),
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddMinutes(1),
            "TestProvider",
            null
        );

        var actions = new List<ActionDescriptor>
        {
            new("test.1", "Test Action 1", "Desc 1", new HashSet<ContentKind> { ContentKind.Files }, Array.Empty<string>(), false, false),
            new("test.2", "Test Action 2", "Desc 2", new HashSet<ContentKind> { ContentKind.Files }, Array.Empty<string>(), false, false)
        };

        var registry = new ActionRegistry();
        foreach (var descriptor in actions)
            registry.Register(new TestAction(descriptor));
        var searchEngine = new ActionSearchEngine(registry);

        var vm = new PaletteViewModel();

        // Act
        vm.Initialize(snapshot, registry, searchEngine);

        // Assert
        await vm.SearchTask;
        Assert.Equal("1 file", vm.ContextSummary);
        Assert.Equal(2, vm.FilteredActions.Count);
    }

    [Fact]
    public async Task SearchText_FiltersActionsCaseInsensitively()
    {
        // Arrange
        var vm = PaletteViewModel.CreateFake();
        await vm.SearchTask;

        // Act
        vm.SearchText = "webp";
        await vm.SearchTask;

        // Assert
        Assert.Single(vm.FilteredActions);
        Assert.Equal("Convert to WebP", vm.FilteredActions[0].Action.Descriptor.Title);

        // Act
        vm.SearchText = "SETTINGS";
        await vm.SearchTask;

        // Assert
        Assert.Single(vm.FilteredActions);
        Assert.Equal("Settings", vm.FilteredActions[0].Action.Descriptor.Title);

        // Act
        vm.SearchText = "image";
        await vm.SearchTask;

        // Assert
        Assert.Equal(4, vm.FilteredActions.Count);
        Assert.Contains(vm.FilteredActions, a => a.Action.Descriptor.Id == "powertrove.image.convert-avif");
        Assert.Contains(vm.FilteredActions, a => a.Action.Descriptor.Id == "powertrove.image.remove-metadata");
        Assert.Contains(vm.FilteredActions, a => a.Action.Descriptor.Title == "Convert to WebP");
        Assert.Contains(vm.FilteredActions, a => a.Action.Descriptor.Title == "Resize Images");
    }

    [Fact]
    public async Task SearchText_MatchesConfiguredAliases()
    {
        // Arrange
        var aliases = new Dictionary<string, IReadOnlyList<string>>
        {
            { "powertrove.image.convert-webp", new List<string> { "konvertieren" } },
            { "powertrove.general.settings", new List<string> { "einstellungen" } }
        };
        var vm = PaletteViewModel.CreateFake(aliases);
        await vm.SearchTask;

        // Act - Match alias for Convert to WebP
        vm.SearchText = "konvertieren";
        await vm.SearchTask;

        // Assert
        Assert.Single(vm.FilteredActions);
        Assert.Equal("Convert to WebP", vm.FilteredActions[0].Action.Descriptor.Title);

        // Act - Match alias for Settings
        vm.SearchText = "einstellungen";
        await vm.SearchTask;

        // Assert
        Assert.Single(vm.FilteredActions);
        Assert.Equal("Settings", vm.FilteredActions[0].Action.Descriptor.Title);
    }

    [Fact]
    public async Task MoveSelection_IteratesOverFilteredActionsAndWraps()
    {
        // Arrange
        var vm = PaletteViewModel.CreateFake();
        await vm.SearchTask;

        // Act & Assert
        // Initially, the first action should be selected (due to default selection logic)
        Assert.NotNull(vm.SelectedAction);
        Assert.Equal(vm.FilteredActions[0].Action.Descriptor.Id, vm.SelectedAction.Action.Descriptor.Id);

        for (int i = 1; i <= vm.FilteredActions.Count; i++)
        {
            vm.MoveSelectionDown();
            Assert.Equal(vm.FilteredActions[i % vm.FilteredActions.Count].Action.Descriptor.Id, vm.SelectedAction!.Action.Descriptor.Id);
        }

        for (int i = vm.FilteredActions.Count - 1; i >= 0; i--)
        {
            vm.MoveSelectionUp();
            Assert.Equal(vm.FilteredActions[i].Action.Descriptor.Id, vm.SelectedAction!.Action.Descriptor.Id);
        }
    }

    [Fact]
    public async Task UpdateFilteredActions_SelectsFirstResultForNewQuery()
    {
        // Arrange
        var vm = PaletteViewModel.CreateFake();
        await vm.SearchTask;
        vm.SelectedAction = vm.FilteredActions.Single(a => a.Action.Descriptor.Id == "powertrove.image.resize");
        Assert.Equal("Resize Images", vm.SelectedAction!.Action.Descriptor.Title);

        // Act - Filtering still leaves "Resize Images" in the results
        vm.SearchText = "image";
        await vm.SearchTask;

        // A new query selects its strongest result instead of retaining an old, lower row.
        Assert.Same(vm.FilteredActions[0], vm.SelectedAction);
        Assert.Equal("Convert to AVIF", vm.SelectedAction!.Action.Descriptor.Title);
    }

    [Fact]
    public async Task UpdateFilteredActions_DefaultsToFirstIfSelectedActionNotPresent()
    {
        // Arrange
        var vm = PaletteViewModel.CreateFake();
        await vm.SearchTask;
        vm.SelectedAction = vm.FilteredActions.Single(a => a.Action.Descriptor.Id == "powertrove.image.resize");
        Assert.Equal("Resize Images", vm.SelectedAction!.Action.Descriptor.Title);

        // Act - Filtering removes "Resize Images" from the results
        vm.SearchText = "webp";
        await vm.SearchTask;

        // Assert - Selection defaults to the first available action
        Assert.Equal("Convert to WebP", vm.SelectedAction.Action.Descriptor.Title);
    }

    [Fact]
    public async Task SearchText_EmptyString_ShowsGroupedAvailableActions()
    {
        // Arrange
        var vm = PaletteViewModel.CreateFake();
        await vm.SearchTask;
        vm.SearchText = "webp";
        await vm.SearchTask;
        Assert.Single(vm.FilteredActions);

        // Act
        vm.SearchText = "";
        await vm.SearchTask;

        // Assert
        Assert.Equal(new[]
        {
            "powertrove.image.convert-webp",
            "powertrove.image.convert-avif",
            "powertrove.image.resize",
            "powertrove.image.remove-metadata",
            "powertrove.fake.fail",
            "powertrove.fake.slow",
            "powertrove.general.settings"
        }, vm.FilteredActions.Select(action => action.Action.Descriptor.Id));
    }

    [Fact]
    public async Task CreateFake_InitializesCorrectly()
    {
        // Act
        var vm = PaletteViewModel.CreateFake();
        await vm.SearchTask;

        // Assert
        Assert.Equal("2 images", vm.ContextSummary);
        Assert.Equal(new[]
        {
            "powertrove.image.convert-webp",
            "powertrove.image.convert-avif",
            "powertrove.image.resize",
            "powertrove.image.remove-metadata",
            "powertrove.fake.fail",
            "powertrove.fake.slow",
            "powertrove.general.settings"
        }, vm.FilteredActions.Select(action => action.Action.Descriptor.Id));
        Assert.Empty(vm.SearchText);
    }

    private sealed class TestAction : IAction
    {
        public ActionDescriptor Descriptor { get; }
        public TestAction(ActionDescriptor descriptor) => Descriptor = descriptor;
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) =>
            new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) =>
            new(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false));
    }

    private sealed class ManualProgressAction : IAction
    {
        public ActionDescriptor Descriptor { get; }
        public ManualProgressAction() => Descriptor = new ActionDescriptor("test.progress", "Progress", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false);

        public ActionProgress[] Reports { get; set; } = Array.Empty<ActionProgress>();

        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) =>
            new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) =>
            new(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            foreach (var report in Reports)
            {
                progress?.Report(report);
            }
            if (System.Threading.SynchronizationContext.Current is InlineSynchronizationContext syncCtx)
            {
                syncCtx.Pump();
            }

            return Task.FromResult(new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false));
        }
    }

    [Fact]
    public async Task ExecuteSelectedActionAsync_FractionalProgress_MapsToPercentage()
    {
        var action = new ManualProgressAction();
        var registry = new ActionRegistry();
        registry.Register(action);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var vm = new PaletteViewModel();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        vm.Initialize(snapshot, registry, searchEngine);
        await vm.SearchTask!;

        vm.SelectedAction = vm.FilteredActions.First(a => a.Action.Descriptor.Id == "test.progress");

        async Task RunAndAssertProgress(ActionProgress[] reports, double expectedProgress)
        {
            action.Reports = reports;

            // To make this deterministic without arbitrary ThreadPool delays, we can install a custom
            // SynchronizationContext for the duration of this method to run the continuations inline.
            var prevCtx = SynchronizationContext.Current;
            var syncCtx = new InlineSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(syncCtx);

            try
            {
                await vm.ExecuteSelectedActionAsync();

                // Process any remaining posted delegates
                syncCtx.Pump();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
            }

            Assert.Equal(expectedProgress, vm.ExecutionProgress);
        }

        // Test normal fractional mapping
        await RunAndAssertProgress(new[] { new ActionProgress(0) }, 0);
        await RunAndAssertProgress(new[] { new ActionProgress(0.5) }, 50);
        await RunAndAssertProgress(new[] { new ActionProgress(1) }, 100);

        // Test out of bounds clamping
        await RunAndAssertProgress(new[] { new ActionProgress(1.5) }, 100);
        await RunAndAssertProgress(new[] { new ActionProgress(-0.5) }, 0);

        // Test non-finite values are ignored (ExecutionProgress stays at previous value, which gets reset to 0 at start,
        // then is updated to 75 by the valid report).
        await RunAndAssertProgress(new[] { new ActionProgress(0.75), new ActionProgress(double.NaN) }, 75);
        await RunAndAssertProgress(new[] { new ActionProgress(0.25), new ActionProgress(double.PositiveInfinity) }, 25);
    }

    [Fact]
    public async Task ExecuteSelectedActionAsync_Success_UpdatesState()
    {
        var action = new TestAction(new ActionDescriptor("test.success", "Success", "",
            new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false));
        var registry = new ActionRegistry();
        registry.Register(action);
        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);
        var vm = new PaletteViewModel();
        vm.Initialize(snapshot, registry, new ActionSearchEngine(registry));
        await vm.SearchTask;
        Assert.NotNull(vm.SelectedAction);
        Assert.Equal(action.Descriptor.Id, vm.SelectedAction.Action.Descriptor.Id);

        Assert.False(vm.IsExecuting);
        Assert.False(vm.HasExecutionResult);

        await vm.ExecuteSelectedActionAsync();

        Assert.False(vm.IsExecuting);
        Assert.True(vm.HasExecutionResult);
        Assert.True(vm.ExecutionIsSuccess);
        Assert.Equal(PowerTrove.Application.Resources.Strings.GetString("Execution_Completed"), vm.ExecutionResultMessage);
    }


    [Fact]
    public async Task ExecuteSelectedActionAsync_ShouldSetOutputs_WhenSuccess()
    {
        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);
        var registry = new ActionRegistry();

        var descriptor = new ActionDescriptor("test.action", "Test", "Desc", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false);
        var receipt = new ActionReceipt(true, false, new[] { "C:/test/out1.txt", "C:/test/out2.txt" }, Array.Empty<ActionFailure>(), false, null);

        registry.Register(new FakeActionWithReceipt(descriptor, receipt));

        var aliases = new Dictionary<string, IReadOnlyList<string>>();
        var searchEngine = new ActionSearchEngine(registry, aliases);

        var vm = new PaletteViewModel();
        vm.Initialize(snapshot, registry, searchEngine);
        await vm.SearchTask;

        vm.SearchText = "Test";
        await vm.SearchTask;

        await vm.ExecuteSelectedActionAsync();

        Assert.True(vm.HasExecutionResult);
        Assert.True(vm.ExecutionIsSuccess);
        Assert.True(vm.HasOutputs);
        Assert.Equal(2, vm.CurrentOutputPaths.Count);
        Assert.Equal(string.Format(PowerTrove.Application.Resources.Strings.GetString("Execution_Completed_Outputs_Other"), 2), vm.ExecutionResultMessage);
    }
    [Fact]
    public async Task ExecuteSelectedActionAsync_Failure_UpdatesState()
    {
        var vm = PaletteViewModel.CreateFake();
        await vm.SearchTask;

        vm.SearchText = "Failing Action";
        await vm.SearchTask;
        Assert.NotNull(vm.SelectedAction);

        await vm.ExecuteSelectedActionAsync();

        Assert.False(vm.IsExecuting);
        Assert.True(vm.HasExecutionResult);
        Assert.False(vm.ExecutionIsSuccess);
        Assert.Contains("Simulated failure occurred", vm.ExecutionResultMessage);
    }

    [Fact]
    public async Task CancelExecution_AbortsAndUpdatesState()
    {
        var vm = PaletteViewModel.CreateFake();
        await vm.SearchTask;

        vm.SearchText = "Slow Action";
        await vm.SearchTask;
        Assert.NotNull(vm.SelectedAction);

        var execTask = vm.ExecuteSelectedActionAsync();

        Assert.True(vm.IsExecuting);
        Assert.False(vm.ShowCancelConfirmation);

        vm.RequestCancelConfirmation();
        Assert.True(vm.ShowCancelConfirmation);

        vm.ConfirmCancel();
        Assert.False(vm.ShowCancelConfirmation);

        await execTask;

        Assert.False(vm.IsExecuting);
        Assert.True(vm.HasExecutionResult);
        Assert.False(vm.ExecutionIsSuccess);
        Assert.Equal(PowerTrove.Application.Resources.Strings.GetString("Execution_Canceled"), vm.ExecutionResultMessage);
    }


    private class FakeActionWithReceipt : IAction
    {
        public ActionDescriptor Descriptor { get; }
        private readonly ActionReceipt _receipt;

        public FakeActionWithReceipt(ActionDescriptor descriptor, ActionReceipt receipt)
        {
            Descriptor = descriptor;
            _receipt = receipt;
        }

        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) =>
            new ValueTask<ActionAvailability>(new ActionAvailability(true));

        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) =>
            new ValueTask<ActionPreview>(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));

        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(_receipt);
    }
    [Fact]
    public async Task CancelExecution_ConfirmationDeclined_Continues()
    {
        var vm = PaletteViewModel.CreateFake();
        await vm.SearchTask;

        vm.SearchText = "Slow Action";
        await vm.SearchTask;
        Assert.NotNull(vm.SelectedAction);

        var execTask = vm.ExecuteSelectedActionAsync();

        Assert.True(vm.IsExecuting);

        vm.RequestCancelConfirmation();
        Assert.True(vm.ShowCancelConfirmation);

        vm.DismissCancelConfirmation();
        Assert.False(vm.ShowCancelConfirmation);
        Assert.True(vm.IsExecuting);

        // Ensure we can cancel eventually
        vm.ConfirmCancel();
        await execTask;
    }

    [Fact]
    public async Task CancelExecution_CompletesWhileConfirmationOpen_Dismisses()
    {
        var vm = PaletteViewModel.CreateFake();
        await vm.SearchTask;

        vm.SearchText = "Slow Action";
        await vm.SearchTask;

        var execTask = vm.ExecuteSelectedActionAsync();

        vm.RequestCancelConfirmation();
        Assert.True(vm.ShowCancelConfirmation);

        // Wait for it to finish naturally without canceling
        await execTask;

        Assert.False(vm.ShowCancelConfirmation);
    }

    private sealed class DelayedSearchEngine : IActionSearchEngine
    {
        private readonly Dictionary<string, TaskCompletionSource<IReadOnlyList<ActionSearchResult>>> _requests = new();

        public void RefreshSignals() { }
        public Task<IReadOnlyList<ActionSearchResult>> SearchAsync(string query, ContextSnapshot context, CancellationToken cancellationToken = default)
        {
            if (query.Length == 0)
                return Task.FromResult<IReadOnlyList<ActionSearchResult>>(Array.Empty<ActionSearchResult>());

            var request = new TaskCompletionSource<IReadOnlyList<ActionSearchResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Add(query, request);
            return request.Task;
        }

        public void Complete(string query, IAction action) =>
            _requests[query].SetResult(new[] { new ActionSearchResult(action, true, null, 100) });
    }

    [Fact]
    public async Task SearchText_OutOfOrderCompletionCannotRestoreStaleResults()
    {
        var earlier = new TestAction(new ActionDescriptor("first", "First", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false));
        var later = new TestAction(new ActionDescriptor("second", "Second", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false));
        var engine = new DelayedSearchEngine();
        var vm = new PaletteViewModel();
        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var registry = new ActionRegistry();
        registry.Register(earlier);
        registry.Register(later);
        vm.Initialize(snapshot, registry, engine);
        await vm.SearchTask;

        vm.SearchText = "first";
        var firstSearch = vm.SearchTask;
        vm.SearchText = "second";
        var secondSearch = vm.SearchTask;

        engine.Complete("second", later);
        await secondSearch;
        Assert.Equal("Second", Assert.Single(vm.FilteredActions).Action.Descriptor.Title);

        engine.Complete("first", earlier);
        await firstSearch;
        Assert.Equal("Second", Assert.Single(vm.FilteredActions).Action.Descriptor.Title);
    }

    [Fact]
    public async Task Clear_LateSearchCompletionCannotRestoreDisplayedGroups()
    {
        var action = new TestAction(new ActionDescriptor("test", "Test", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false));
        var engine = new DelayedSearchEngine();
        var vm = new PaletteViewModel();
        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);
        var registry = new ActionRegistry();
        registry.Register(action);
        vm.Initialize(snapshot, registry, engine);
        await vm.SearchTask;

        vm.SearchText = "first";
        engine.Complete("first", action);
        await vm.SearchTask;
        Assert.NotEmpty(Assert.Single(vm.Groups).Items);

        vm.SearchText = "pending";
        var pendingSearch = vm.SearchTask;
        vm.Clear();
        Assert.Empty(vm.Groups);

        // This engine deliberately completes even after cancellation.
        engine.Complete("pending", action);
        await pendingSearch;

        Assert.Empty(vm.Groups);
        Assert.Empty(vm.FilteredActions);
        Assert.Null(vm.SelectedAction);
    }

    private sealed class FailingSearchEngine : IActionSearchEngine
    {
        public void RefreshSignals() { }
        public Task<IReadOnlyList<ActionSearchResult>> SearchAsync(
            string query, ContextSnapshot context, CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<ActionSearchResult>>(
                new InvalidOperationException("Sensitive details must never be displayed."));
    }

    [Fact]
    public async Task SearchFailure_ReportsSafeErrorAndDoesNotExposeException()
    {
        var vm = new PaletteViewModel();
        var snapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available,
            new NoContent(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1),
            "test",
            null);
        vm.Initialize(snapshot, new ActionRegistry(), new FailingSearchEngine());
        await vm.SearchTask;

        Assert.Empty(vm.FilteredActions);
        Assert.Equal("Action search is temporarily unavailable.", vm.SearchError);
        Assert.DoesNotContain("Sensitive", vm.SearchError);
    }

    [Fact]
    public async Task InitialState_IsEmpty()
    {
        var vm = new PaletteViewModel();
        var snapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 0, null, DateTimeOffset.UtcNow),
            ContextStatus.Empty,
            new NoContent(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "startup",
            "Ready");

        var registry = new ActionRegistry();
        vm.Initialize(snapshot, registry, new ActionSearchEngine(registry));
        await vm.SearchTask;

        Assert.Equal("Ready", vm.ContextSummary);
        Assert.Empty(vm.FilteredActions);
    }

    [Fact]
    public async Task Transition_To_Available_Snapshot()
    {
        var vm = new PaletteViewModel();
        var emptySnapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 0, null, DateTimeOffset.UtcNow),
            ContextStatus.Empty,
            new NoContent(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "startup",
            "Ready");

        var registry = new ActionRegistry();
        var searchEngine = new ActionSearchEngine(registry);
        vm.Initialize(emptySnapshot, registry, searchEngine);
        await vm.SearchTask;

        Assert.Equal("Ready", vm.ContextSummary);
        Assert.Empty(vm.FilteredActions);

        var availableSnapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1234, "explorer.exe", DateTimeOffset.UtcNow),
            ContextStatus.Available,
            new FileSelection(new[]
            {
                new SelectedFile("C:\\Fake\\image1.png", "image1.png", ".png", "image/png", 1024, true, true, null, false)
            }),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "explorer",
            null);

        vm.Initialize(availableSnapshot, registry, searchEngine);
        await vm.SearchTask;

        Assert.Equal("1 image", vm.ContextSummary);
    }

    [Fact]
    public void DismissFirstRun_SetsShowFirstRunToFalse()
    {
        var vm = new PaletteViewModel();
        vm.ShowFirstRun = true;
        Assert.True(vm.ShowFirstRun);

        vm.DismissFirstRun();

        Assert.False(vm.ShowFirstRun);
    }

    [Fact]
    public void GenerateSummary_UnsupportedApp_ReturnsGracefulString()
    {
        var vm = new PaletteViewModel();
        var snapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Failed,
            new NoContent(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1),
            "test",
            null);

        vm.Initialize(snapshot, new ActionRegistry(), new FailingSearchEngine());

        Assert.Equal("Selection not available", vm.ContextSummary);
        Assert.Contains("could not be read", vm.ContextNotice);
    }

    [Fact]
    public void GenerateSummary_FileSelection_CountsTypesAndLocalAvailability()
    {
        var vm = new PaletteViewModel();

        var selectedFiles = new List<SelectedFile>
        {
            new SelectedFile("C:\\image1.png", "image1.png", ".png", "image/png", 1024, true, true, null, false),
            new SelectedFile("C:\\image2.jpg", "image2.jpg", ".jpg", "image/jpeg", 2048, true, true, null, false),
            new SelectedFile("C:\\folder1", "folder1", "", null, null, true, true, null, true),
            new SelectedFile("C:\\file1.txt", "file1.txt", ".txt", "text/plain", 512, false, true, null, false), // Offline
            new SelectedFile("C:\\file2.pdf", "file2.pdf", ".pdf", "application/pdf", 1024, true, false, null, false) // Inaccessible
        };

        var snapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available,
            new FileSelection(selectedFiles),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1),
            "test",
            null);

        vm.Initialize(snapshot, new ActionRegistry(), new FailingSearchEngine());

        Assert.Equal("2 images, 1 folder, 2 files (2 offline)", vm.ContextSummary);
    }

    [Fact]
    public void GenerateSummary_TableSelection_CountsDimensions()
    {
        var vm = new PaletteViewModel();

        var rows = new List<IReadOnlyList<CellValue>>
        {
            new List<CellValue> { new TextCell("Header1"), new TextCell("Header2"), new TextCell("Header3") },
            new List<CellValue> { new TextCell("Value1"), new EmptyCell() }, // Missing one column
            new List<CellValue> { new TextCell("Value3"), new TextCell("Value4"), new TextCell("Value5"), new TextCell("Extra") } // Extra column
        };

        var snapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available,
            new TableData(rows, "TestApp", "A1:D3"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1),
            "test",
            null);

        vm.Initialize(snapshot, new ActionRegistry(), new FailingSearchEngine());

        Assert.Equal("Table selected (3 rows, 4 columns)", vm.ContextSummary);
    }

    [Fact]
    public void GenerateSummary_TableSelection_EmptyTable_ReturnsZeroes()
    {
        var vm = new PaletteViewModel();

        var snapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available,
            new TableData(new List<IReadOnlyList<CellValue>>(), "TestApp", null),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1),
            "test",
            null);

        vm.Initialize(snapshot, new ActionRegistry(), new FailingSearchEngine());

        Assert.Equal("Table selected (0 rows, 0 columns)", vm.ContextSummary);
    }

    [Fact]
    public async Task ExecuteSelectedActionAsync_Incompatible_DoesNotExecute()
    {
        var action = new TestAction(new ActionDescriptor("test.incompatible", "Incompatible", "",
            new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false));
        var registry = new ActionRegistry();
        registry.Register(action);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var vm = new PaletteViewModel();
        var searchEngine = new MockIncompatibleSearchEngine(action, "Wrong input kind");
        vm.Initialize(snapshot, registry, searchEngine);
        await vm.SearchTask;

        Assert.Null(vm.SelectedAction);
        vm.IncludeUnavailableActions = true;
        Assert.NotNull(vm.SelectedAction);
        Assert.False(vm.SelectedAction.IsCompatible);

        await vm.ExecuteSelectedActionAsync();

        Assert.False(vm.IsExecuting);
        Assert.False(vm.HasExecutionResult);
    }

    [Fact]
    public async Task SearchAsync_IncompatibleResults_SurvivePresentation()
    {
        var action1 = new TestAction(new ActionDescriptor("test.incompatible1", "Incompatible1", "",
            new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false));
        var action2 = new TestAction(new ActionDescriptor("test.incompatible2", "Incompatible2", "",
            new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false));
        var action3 = new TestAction(new ActionDescriptor("test.incompatible3", "Incompatible3", "",
            new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false));

        var registry = new ActionRegistry();
        registry.Register(action1);
        registry.Register(action2);
        registry.Register(action3);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var vm = new PaletteViewModel();
        var searchEngine = new MockMultipleIncompatibleSearchEngine(action1, action2, action3);
        vm.Initialize(snapshot, registry, searchEngine);
        await vm.SearchTask;

        Assert.Empty(vm.FilteredActions);
        Assert.Equal(3, vm.UnavailableActionCount);
        vm.IncludeUnavailableActions = true;
        Assert.Equal(3, vm.FilteredActions.Count);

        var res1 = vm.FilteredActions[0];
        Assert.False(res1.IsCompatible);
        Assert.Equal("Wrong input kind", res1.IncompatibilityReason);

        var res2 = vm.FilteredActions[1];
        Assert.False(res2.IsCompatible);
        Assert.Equal("Missing capability", res2.IncompatibilityReason);

        var res3 = vm.FilteredActions[2];
        Assert.False(res3.IsCompatible);
        Assert.Equal("Action availability could not be determined.", res3.IncompatibilityReason);
    }

    private sealed class MockIncompatibleSearchEngine : IActionSearchEngine
    {
        private readonly IAction _action;
        private readonly string _reason;

        public MockIncompatibleSearchEngine(IAction action, string reason)
        {
            _action = action;
            _reason = reason;
        }

        public void RefreshSignals() { }
        public Task<IReadOnlyList<ActionSearchResult>> SearchAsync(string query, ContextSnapshot context, CancellationToken cancellationToken = default)
        {
            var results = new List<ActionSearchResult>
            {
                new ActionSearchResult(_action, false, _reason, 100)
            };
            return Task.FromResult<IReadOnlyList<ActionSearchResult>>(results);
        }
    }

    private sealed class MockMultipleIncompatibleSearchEngine : IActionSearchEngine
    {
        private readonly IAction _action1;
        private readonly IAction _action2;
        private readonly IAction _action3;

        public MockMultipleIncompatibleSearchEngine(IAction action1, IAction action2, IAction action3)
        {
            _action1 = action1;
            _action2 = action2;
            _action3 = action3;
        }

        public void RefreshSignals() { }
        public Task<IReadOnlyList<ActionSearchResult>> SearchAsync(string query, ContextSnapshot context, CancellationToken cancellationToken = default)
        {
            var results = new List<ActionSearchResult>
            {
                new ActionSearchResult(_action1, false, "Wrong input kind", 100),
                new ActionSearchResult(_action2, false, "Missing capability", 90),
                new ActionSearchResult(_action3, false, "Action availability could not be determined.", 80)
            };
            return Task.FromResult<IReadOnlyList<ActionSearchResult>>(results);
        }
    }


    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<Tuple<SendOrPostCallback, object?>> _items = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            _items.Enqueue(Tuple.Create(d, state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            d(state);
        }

        public void Pump()
        {
            while (_items.TryDequeue(out var item))
            {
                item.Item1(item.Item2);
            }
        }
    }

    private sealed class DelayedProgressAction : IAction
    {
        public ActionDescriptor Descriptor { get; }
        private readonly TaskCompletionSource _executionStarted = new();
        private readonly TaskCompletionSource _continueExecution = new();

        public DelayedProgressAction()
        {
            Descriptor = new ActionDescriptor("delayed.progress", "Delayed", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false);
        }

        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) =>
            new ValueTask<ActionAvailability>(new ActionAvailability(true));

        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) =>
            new ValueTask<ActionPreview>(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));

        public async Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(new ActionProgress(10, null, "First"));
            _executionStarted.SetResult();

            try {
                using var reg = cancellationToken.Register(() => _continueExecution.TrySetCanceled());
                await _continueExecution.Task;
            } catch (OperationCanceledException) {
                // Simulate a rogue action that catches cancellation but still reports progress
                progress?.Report(new ActionProgress(99, null, "Delayed"));
                throw; // But correctly throws eventually
            }

            return new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false, "Done");
        }

        public Task WaitForExecutionStartedAsync() => _executionStarted.Task;
    }

    [Fact]
    public async Task ExecuteSelectedActionAsync_DelayedProgress_IgnoredAfterNewRunStarts()
    {
        var action = new DelayedProgressAction();
        var registry = new ActionRegistry();
        registry.Register(action);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var vm = new PaletteViewModel();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        vm.Initialize(snapshot, registry, searchEngine);
        await vm.SearchTask!;

        vm.SelectedAction = vm.FilteredActions[0];

        var firstExecTask = vm.ExecuteSelectedActionAsync();

        // Wait for execution to actually start and report First
        await action.WaitForExecutionStartedAsync();

        // Let event loop process progress callback
        await Task.Yield();

        // Cancel first run
        vm.CancelExecution();
        await firstExecTask;

        Assert.Equal(PowerTrove.Application.Resources.Strings.GetString("Execution_Canceled"), vm.ExecutionResultMessage);

        // Start second run
        var action2 = new TestAction(new ActionDescriptor("test.quick", "Quick", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false));
        registry.Register(action2);
        vm.Initialize(snapshot, registry, searchEngine);
        await vm.SearchTask!;

        vm.SelectedAction = vm.FilteredActions.First(a => a.Action.Descriptor.Id == "test.quick");
        await vm.ExecuteSelectedActionAsync();

        // Let event loop process any pending delayed progress events
        await Task.Yield();

        // The delayed progress should NOT override the UI
        Assert.NotEqual("Delayed", vm.ExecutionStatusMessage);
    }

    [Fact]
    public async Task ExecuteAction_WithKeepResultVisible_LeavesPanelOpen()
    {
        var action = new TestAction(new ActionDescriptor("test.action", "Test", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), true, true));
        var registry = new ActionRegistry();
        registry.Register(action);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var vm = new PaletteViewModel();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        vm.Initialize(snapshot, registry, searchEngine);

        var mockSettings = new MockSettingsService();
        mockSettings.Settings = UserSettings.CreateDefault() with { AfterFileAction = PowerTrove.Application.Configuration.AfterFileAction.KeepResultVisible };
        vm.SettingsService = mockSettings;

        bool dismissed = false;
        vm.DismissRequested += (s, e) => dismissed = true;

        await vm.SearchTask!;
        vm.SelectedAction = vm.FilteredActions.First();

        await vm.ExecuteSelectedActionAsync();

        Assert.True(vm.HasExecutionResult);
        Assert.False(dismissed);
    }

    [Fact]
    public async Task ExecuteAction_WithClosePalette_FiresDismissRequested()
    {
        var action = new TestAction(new ActionDescriptor("test.action", "Test", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), true, true));
        var registry = new ActionRegistry();
        registry.Register(action);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var vm = new PaletteViewModel();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        vm.Initialize(snapshot, registry, searchEngine);

        var mockSettings = new MockSettingsService();
        mockSettings.Settings = UserSettings.CreateDefault() with { AfterFileAction = PowerTrove.Application.Configuration.AfterFileAction.ClosePalette };
        vm.SettingsService = mockSettings;

        bool dismissed = false;
        vm.DismissRequested += (s, e) => dismissed = true;

        await vm.SearchTask!;
        vm.SelectedAction = vm.FilteredActions.First();

        await vm.ExecuteSelectedActionAsync();

        Assert.True(dismissed);
    }

    [Fact]
    public async Task ExecuteAction_SetsOutputAndFailureCounts()
    {
        var action = new TestFailAction(new ActionDescriptor("test.fail", "Test", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), true, true));
        var registry = new ActionRegistry();
        registry.Register(action);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var vm = new PaletteViewModel();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        vm.Initialize(snapshot, registry, searchEngine);

        await vm.SearchTask!;
        vm.SelectedAction = vm.FilteredActions.First();

        await vm.ExecuteSelectedActionAsync();

        Assert.Equal(0, vm.OutputCount);
        Assert.Equal(1, vm.FailureCount);
        Assert.DoesNotContain("C:\\", vm.ExecutionResultMessage);
    }

    [Fact]
    public async Task OpenSingleOutput_AvailableOnlyWhenOneOutput()
    {
        var action = new MockSingleOutputAction();
        var registry = new ActionRegistry();
        registry.Register(action);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var vm = new PaletteViewModel();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        vm.Initialize(snapshot, registry, searchEngine);

        await vm.SearchTask!;
        vm.SelectedAction = vm.FilteredActions.First();

        await vm.ExecuteSelectedActionAsync();

        Assert.Equal(1, vm.OutputCount);
        Assert.True(vm.CanOpenSingleOutput);
    }

    [Fact]
    public async Task ExecuteSelectedActionAsync_WithClipboardText_WritesToClipboard()
    {
        var action = new ClipboardAction("Short text");
        var registry = new ActionRegistry();
        registry.Register(action);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var vm = new PaletteViewModel();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        vm.Initialize(snapshot, registry, searchEngine);

        var mockWriter = new MockClipboardWriter();
        vm.ClipboardWriter = mockWriter;

        await vm.SearchTask!;
        vm.SelectedAction = vm.FilteredActions.First();

        await vm.ExecuteSelectedActionAsync();

        Assert.Equal(1, mockWriter.WriteCount);
        Assert.Equal("Short text", mockWriter.LastWrittenText);
        Assert.Contains("Copied to clipboard.", vm.ExecutionResultMessage);
    }

    [Fact]
    public async Task ExecuteSelectedActionAsync_WithOversizedClipboardText_RefusesWrite()
    {
        var action = new ClipboardAction(new string('a', 10 * 1024 * 1024 + 1));
        var registry = new ActionRegistry();
        registry.Register(action);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var vm = new PaletteViewModel();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        vm.Initialize(snapshot, registry, searchEngine);

        var mockWriter = new MockClipboardWriter();
        vm.ClipboardWriter = mockWriter;

        await vm.SearchTask!;
        vm.SelectedAction = vm.FilteredActions.First();

        await vm.ExecuteSelectedActionAsync();

        Assert.Equal(0, mockWriter.WriteCount);
        Assert.Contains("Clipboard text exceeded 10 MB limit and was not copied.", vm.ExecutionResultMessage);
    }

    [Fact]
    public async Task ExecuteSelectedActionAsync_Failure_DoesNotWriteToClipboard()
    {
        var action = new ClipboardAction("Should not copy", fail: true);
        var registry = new ActionRegistry();
        registry.Register(action);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var vm = new PaletteViewModel();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        vm.Initialize(snapshot, registry, searchEngine);

        var mockWriter = new MockClipboardWriter();
        vm.ClipboardWriter = mockWriter;

        await vm.SearchTask!;
        vm.SelectedAction = vm.FilteredActions.First();

        await vm.ExecuteSelectedActionAsync();

        Assert.Equal(0, mockWriter.WriteCount);
        Assert.DoesNotContain("Copied to clipboard.", vm.ExecutionResultMessage);
    }

    [Fact]
    public async Task ExecuteSelectedActionAsync_ClipboardBusy_KeepsActionSuccessful()
    {
        var action = new ClipboardAction("Short text");
        var registry = new ActionRegistry();
        registry.Register(action);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

        var vm = new PaletteViewModel();
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
        vm.Initialize(snapshot, registry, searchEngine);
        vm.ClipboardWriter = new BusyClipboardWriter();

        await vm.SearchTask!;
        vm.SelectedAction = vm.FilteredActions.First();

        await vm.ExecuteSelectedActionAsync();

        Assert.True(vm.ExecutionIsSuccess);
        Assert.Contains("Could not copy to the clipboard.", vm.ExecutionResultMessage);
        Assert.DoesNotContain("Short text", vm.ExecutionResultMessage);
    }

    private sealed class BusyClipboardWriter : IClipboardWriter
    {
        public Task WriteTextAsync(string text) => throw new InvalidOperationException("clipboard locked");
    }

    private class MockClipboardWriter : IClipboardWriter
    {
        public int WriteCount { get; private set; }
        public string? LastWrittenText { get; private set; }

        public Task WriteTextAsync(string text)
        {
            WriteCount++;
            LastWrittenText = text;
            return Task.CompletedTask;
        }
    }

    private class ClipboardAction : IAction
    {
        private readonly string _text;
        private readonly bool _fail;
        public ActionDescriptor Descriptor { get; }
        public ClipboardAction(string text, bool fail = false)
        {
            _text = text;
            _fail = fail;
            Descriptor = new ActionDescriptor("test.clipboard", "Test", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), true, true);
        }
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) => new(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ActionReceipt(!_fail, false, Array.Empty<string>(), _fail ? new[] { new ActionFailure(null, "Failed") } : Array.Empty<ActionFailure>(), false, ClipboardText: _text));
        }
    }

    private class MockSingleOutputAction : IAction
    {
        public ActionDescriptor Descriptor { get; }
        public MockSingleOutputAction() => Descriptor = new ActionDescriptor("test.single", "Test", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), true, true);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) => new(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ActionReceipt(true, false, new[] { "C:\\out.txt" }, Array.Empty<ActionFailure>(), false));
        }
    }

    private class MockSettingsService : ISettingsService
    {
        public UserSettings Settings { get; set; } = UserSettings.CreateDefault();
        public bool IsReadOnly => false;
        public Task SaveAsync() => Task.CompletedTask;
        public void PinAction(string actionId) {}
        public void UnpinAction(string actionId) {}
        public void RecordActionExecution(string actionId) {}
        public void SetActionPreference(string actionId, string key, string value) {}
        public void SetActionPreference(ActionDescriptor descriptor, string key, string value) {}
        public void SetGlobalShortcut(HotkeyConfiguration shortcut) {}
        public void ClearRecentActions() {}
        public void ResetToDefaults() {}
        public void AddImageExportPreset(SavedImageExportPreset preset) {}
        public void RemoveImageExportPreset(string id) {}
        public void AddActionHistory(ActionHistoryItem item) {}
        public void MarkFirstRunSeen() {}
        public void RecordSelectionUsage(string signatureKey, string actionId, DateTimeOffset now) {}
        public void ClearSelectionUsage() {}
        public void SetUiLanguage(string? language) {}
        public void SetRunFileToolsInSeparateProcess(bool enabled) {}
    }

    private class TestFailAction : IAction
    {
        public ActionDescriptor Descriptor { get; }
        public TestFailAction(ActionDescriptor descriptor) => Descriptor = descriptor;
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) => new(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure("C:\\test\\file.txt", "Failed to process") }, false));
        }
    }


    private class MockSettingsServiceForUsage : ISettingsService
    {
        public UserSettings Settings => UserSettings.CreateDefault();
        public bool IsReadOnly => false;
        public int RecordCount = 0;
        public Task SaveAsync() => Task.CompletedTask;
        public void PinAction(string actionId) {}
        public void UnpinAction(string actionId) {}
        public void RecordActionExecution(string actionId) {}
        public void SetActionPreference(string actionId, string key, string value) {}
        public void SetActionPreference(ActionDescriptor descriptor, string key, string value) {}
        public void SetGlobalShortcut(HotkeyConfiguration shortcut) {}
        public void ClearRecentActions() {}
        public void ResetToDefaults() {}
        public void AddImageExportPreset(SavedImageExportPreset preset) {}
        public void RemoveImageExportPreset(string id) {}
        public void AddActionHistory(ActionHistoryItem item) {}
        public void MarkFirstRunSeen() {}
        public void RecordSelectionUsage(string signatureKey, string actionId, DateTimeOffset now) { RecordCount++; }
        public void ClearSelectionUsage() {}
        public void SetUiLanguage(string? language) {}
        public void SetRunFileToolsInSeparateProcess(bool enabled) {}
    }

    [Fact]
    public async Task ExecuteSelectedActionAsync_Success_RecordsSelectionUsage()
    {
        var action = new TestAction(new ActionDescriptor("test.success", "Success", "",
            new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false));
        var registry = new ActionRegistry();
        registry.Register(action);
        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);
        var searchEngine = new FailingSearchEngine(); // using existing one, just need an object
        var settingsService = new MockSettingsServiceForUsage();
        var vm = new PaletteViewModel();
        vm.Initialize(snapshot, registry, searchEngine, settingsService);
        await vm.SearchTask!;

        vm.SelectedAction = new ActionSearchResult(action, true, null, 100, ActionHighlight.None, PowerTrove.Abstractions.SelectionFit.None);
        await vm.ExecuteSelectedActionAsync();

        Assert.Equal(1, settingsService.RecordCount);
    }

    [Fact]
    public async Task ExecuteSelectedActionAsync_FailureOrCancel_DoesNotRecord()
    {
        var actionFail = new FailingActionForUsage();
        var registry = new ActionRegistry();
        registry.Register(actionFail);
        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);
        var searchEngine = new FailingSearchEngine();
        var settingsService = new MockSettingsServiceForUsage();
        var vm = new PaletteViewModel();
        vm.Initialize(snapshot, registry, searchEngine, settingsService);
        await vm.SearchTask!;

        vm.SelectedAction = new ActionSearchResult(actionFail, true, null, 100, ActionHighlight.None, PowerTrove.Abstractions.SelectionFit.None);
        await vm.ExecuteSelectedActionAsync();

        Assert.Equal(0, settingsService.RecordCount);
    }

    private class FailingActionForUsage : IAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor("test.fail", "Fail", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) => new ValueTask<ActionAvailability>(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) => new ValueTask<ActionPreview>(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, "failed") }, false));
    }
    [Fact]
    public async Task SearchText_Empty_CreatesGroupsWith3SuggestedMax_AndLogicalNavigation()
    {
        // Arrange
        var snapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.Now, "Test Window"),
            ContextStatus.Available,
            new FileSelection(new[] { new SelectedFile("test.txt", "test.txt", ".txt", null, null, true, true, null, false) }),
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddMinutes(1),
            "TestProvider",
            null
        );

        var registry = new ActionRegistry();

        var d1 = new ActionDescriptor("1", "A", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false);
        var d2 = new ActionDescriptor("2", "B", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false);
        var d3 = new ActionDescriptor("3", "C", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false);
        var d4 = new ActionDescriptor("4", "D", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false);
        var d5 = new ActionDescriptor("5", "E", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false);

        var a1 = new FakeAction(d1);
        var a2 = new FakeAction(d2);
        var a3 = new FakeAction(d3);
        var a4 = new FakeAction(d4);
        var a5 = new FakeAction(d5);

        var searchEngine = new MockSearchEngine();

        searchEngine.ResultsToReturn = new List<ActionSearchResult>
        {
            new ActionSearchResult(a1, true, null, 1, ActionHighlight.LastUsedForSelection, SelectionFit.Ideal),
            new ActionSearchResult(a2, true, null, 2, ActionHighlight.Favorite, SelectionFit.None),
            new ActionSearchResult(a3, true, null, 3, ActionHighlight.BestFit, SelectionFit.None),
            new ActionSearchResult(a4, true, null, 4, ActionHighlight.LastUsedForSelection, SelectionFit.None),
            new ActionSearchResult(a5, true, null, 5, ActionHighlight.None, SelectionFit.None)
        };

        var vm = new PaletteViewModel();
        vm.Initialize(snapshot, registry, searchEngine);

        // Act
        vm.SearchText = string.Empty;
        await vm.SearchTask;

        // Assert
        Assert.NotNull(vm.Groups);
        Assert.Equal(2, vm.Groups.Count);
        Assert.Equal("Suggested for this selection", vm.Groups[0].Title);
        Assert.Equal(3, vm.Groups[0].Items.Count); // Capped at 3
        Assert.Equal("1", vm.Groups[0].Items[0].Action.Descriptor.Id);
        Assert.Equal("2", vm.Groups[0].Items[1].Action.Descriptor.Id);
        Assert.Equal("3", vm.Groups[0].Items[2].Action.Descriptor.Id);

        Assert.Equal("All actions", vm.Groups[1].Title);
        Assert.Equal(2, vm.Groups[1].Items.Count);
        Assert.Equal("4", vm.Groups[1].Items[0].Action.Descriptor.Id);
        Assert.Equal("5", vm.Groups[1].Items[1].Action.Descriptor.Id);

        // Test navigation
        vm.SelectedAction = vm.FilteredActions[0];
        vm.MoveSelectionDown();
        Assert.Equal("2", vm.SelectedAction.Action.Descriptor.Id);
        vm.MoveSelectionDown();
        Assert.Equal("3", vm.SelectedAction.Action.Descriptor.Id);
        vm.MoveSelectionDown(); // Should cross group boundary to id 4
        Assert.Equal("4", vm.SelectedAction.Action.Descriptor.Id);
        vm.MoveSelectionDown(); // To 5
        Assert.Equal("5", vm.SelectedAction.Action.Descriptor.Id);
    }

    [Fact]
    public async Task SearchText_NotEmpty_CreatesSingleUnnamedGroup()
    {
        var snapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.Now, "Test Window"),
            ContextStatus.Available,
            new FileSelection(new[] { new SelectedFile("test.txt", "test.txt", ".txt", null, null, true, true, null, false) }),
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddMinutes(1),
            "TestProvider",
            null
        );

        var registry = new ActionRegistry();
        var d1 = new ActionDescriptor("1", "A", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false);
        var searchEngine = new MockSearchEngine();
        searchEngine.ResultsToReturn = new List<ActionSearchResult>
        {
            new ActionSearchResult(new FakeAction(d1), true, null, 1, ActionHighlight.LastUsedForSelection, SelectionFit.Ideal),
        };

        var vm = new PaletteViewModel();
        vm.Initialize(snapshot, registry, searchEngine);

        vm.SearchText = "query";
        await vm.SearchTask;

        Assert.Single(vm.Groups);
        Assert.Equal(string.Empty, vm.Groups[0].Title);
        Assert.Single(vm.Groups[0].Items);
    }

    [Fact]
    public void GetBadgeText_ReturnsCorrectStrings()
    {
        // Fuzzed file names should not appear in badges
        var snapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.Now, "Test"),
            ContextStatus.Available,
            new FileSelection(new[] {
                new SelectedFile(@"C:\sensitive_name.pdf", "sensitive_name.pdf", ".pdf", null, null, true, true, null, false),
                new SelectedFile(@"C:\secret.pdf", "secret.pdf", ".pdf", null, null, true, true, null, false)
            }),
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddMinutes(1),
            "Test",
            null
        );

        var registry = new ActionRegistry();
        var vm = new PaletteViewModel();
        vm.Initialize(snapshot, registry, new MockSearchEngine());

        var fav = new ActionSearchResult(new FakeAction(new ActionDescriptor("1", "A", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false)), true, null, 1, ActionHighlight.Favorite);
        var rec = new ActionSearchResult(new FakeAction(new ActionDescriptor("2", "A", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false)), true, null, 2, ActionHighlight.BestFit);
        var last = new ActionSearchResult(new FakeAction(new ActionDescriptor("3", "A", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false)), true, null, 3, ActionHighlight.LastUsedForSelection);

        Assert.Equal("Favorite", vm.GetBadgeText(fav));
        Assert.Equal("Recommended", vm.GetBadgeText(rec));
        // Pluralization and counts
        Assert.Equal("Last used for 2–5 PDFs", vm.GetBadgeText(last));
    }

    private class MockSearchEngine : IActionSearchEngine
    {
        public List<ActionSearchResult> ResultsToReturn { get; set; } = new();

        public void RefreshSignals() { }

        public Task<IReadOnlyList<ActionSearchResult>> SearchAsync(string query, ContextSnapshot context, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ActionSearchResult>>(ResultsToReturn);
        }
    }
    private class FakeAction : IAction
    {
        public ActionDescriptor Descriptor { get; }
        public FakeAction(ActionDescriptor descriptor) => Descriptor = descriptor;
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) => new(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false));
    }

    [Fact]
    public async Task ExecuteSelectedActionAsync_ProgressDoesNotChangeStateAfterCompletion()
    {
        var action = new TestActionWithProgress();
        var registry = new ActionRegistry();
        registry.Register(action);

        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);
        var searchEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());

        var vm = new PaletteViewModel();
        vm.Initialize(snapshot, registry, searchEngine);
        await vm.SearchTask!;

        vm.SelectedAction = vm.FilteredActions.First();

        await vm.ExecuteSelectedActionAsync();

        Assert.False(vm.IsExecuting);
        Assert.Equal(0, vm.ExecutionProgress); // Initial before change

        // Let's ensure the progress changes after finish don't affect VM
        action.FireProgress(100.0, "Late Update");

        Assert.Equal(0, vm.ExecutionProgress); // Should not change
        Assert.NotEqual("Late Update", vm.ExecutionStatusMessage);
    }

    private class TestActionWithProgress : IAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor("test.progress", "Test Progress", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false);
        private IProgress<ActionProgress>? _progress;

        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) => new ValueTask<ActionAvailability>(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) => new ValueTask<ActionPreview>(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));

        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            _progress = progress;
            return Task.FromResult(new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false));
        }

        public void FireProgress(double percentage, string message)
        {
            _progress?.Report(new ActionProgress(percentage, message));
            if (System.Threading.SynchronizationContext.Current is InlineSynchronizationContext syncCtx)
            {
                syncCtx.Pump();
            }
        }
    }
}
