using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ContextBar.Application.ViewModels;
using ContextBar.Core;
using ContextBar.Core.Search;
using ContextBar.Application.Configuration;

namespace ContextBar.Application.Tests
{
    public class PaletteViewModelSearchTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("Test")]
        public async Task SearchError_ClearsPreviouslyDisplayedResults(string query)
        {
            var registry = new ActionRegistry();
            registry.Register(new SearchErrorAction { ThrowError = false });
            var innerEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
            var searchEngine = new FailingSearchEngine(innerEngine) { ThrowError = false };
            var vm = new PaletteViewModel();
            var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

            vm.Initialize(snapshot, registry, searchEngine);
            await vm.SearchTask;
            vm.SearchText = query;
            await vm.SearchTask;

            Assert.NotEmpty(vm.FilteredActions);
            Assert.NotEmpty(Assert.Single(vm.Groups).Items);
            Assert.NotNull(vm.SelectedAction);
            var changedProperties = new List<string?>();
            vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

            searchEngine.ThrowError = true;
            await vm.RetrySearchAsync();

            Assert.NotNull(vm.SearchError);
            Assert.Null(vm.SelectedAction);
            Assert.Empty(vm.FilteredActions);
            Assert.Empty(vm.Groups);
            Assert.Contains(nameof(PaletteViewModel.Groups), changedProperties);
        }

        [Theory]
        [InlineData("")]
        [InlineData("Test")]
        public async Task Clear_ClearsPreviouslyDisplayedGroups(string query)
        {
            var registry = new ActionRegistry();
            registry.Register(new SearchErrorAction { ThrowError = false });
            var vm = new PaletteViewModel();
            var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);
            vm.Initialize(snapshot, registry, new ActionSearchEngine(registry));
            await vm.SearchTask;
            vm.SearchText = query;
            await vm.SearchTask;

            Assert.NotEmpty(Assert.Single(vm.Groups).Items);
            var changedProperties = new List<string?>();
            vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

            vm.Clear();
            await vm.RetrySearchAsync();

            Assert.Empty(vm.Groups);
            Assert.Empty(vm.FilteredActions);
            Assert.Null(vm.SelectedAction);
            Assert.Contains(nameof(PaletteViewModel.Groups), changedProperties);
        }

        [Fact]
        public async Task Dismissal_InvalidatesRetry()
        {
            var registry = new ActionRegistry();
            var innerEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
            var searchEngine = new FailingSearchEngine(innerEngine);
            var vm = new PaletteViewModel();
            var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

            vm.Initialize(snapshot, registry, searchEngine);
            await vm.SearchTask;

            Assert.NotNull(vm.SearchError);

            searchEngine.ThrowError = false;
            var retryTask = vm.RetrySearchAsync();
            vm.Clear(); // simulate dismissal/clearing

            await retryTask;

            Assert.Null(vm.SearchError); // search error is cleared
            Assert.Null(vm.SelectedAction); // results are ignore because it was cleared
            Assert.Empty(vm.FilteredActions);
        }

        [Fact]
        public async Task RetrySearchAsync_RetriesWithCurrentQuery()
        {
            var registry = new ActionRegistry();
            var action = new SearchErrorAction();
            action.ThrowError = false;
            registry.Register(action);
            var innerEngine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());
            var searchEngine = new FailingSearchEngine(innerEngine);
            var vm = new PaletteViewModel();
            var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

            vm.Initialize(snapshot, registry, searchEngine);
            await vm.SearchTask;

            Assert.NotNull(vm.SearchError);

            searchEngine.ThrowError = false;

            await vm.RetrySearchAsync();

            Assert.Null(vm.SearchError);
            Assert.NotEmpty(vm.FilteredActions);
            Assert.NotNull(vm.SelectedAction);
        }

        private class SearchErrorAction : IAction
        {
            public bool ThrowError { get; set; } = true;
            public ActionDescriptor Descriptor { get; } = new ActionDescriptor("test.error", "Test", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), true, true);
            public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
            {
                if (ThrowError) throw new Exception("Simulated Search Error");
                return new ValueTask<ActionAvailability>(new ActionAvailability(true));
            }
            public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) => new(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));
            public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false));
        }

        private class FailingSearchEngine : IActionSearchEngine
        {
            public bool ThrowError { get; set; } = true;
            private readonly IActionSearchEngine _engine;

            public FailingSearchEngine(IActionSearchEngine engine)
            {
                _engine = engine;
            }

            public void RefreshSignals() { }
        public Task<IReadOnlyList<ActionSearchResult>> SearchAsync(string query, ContextSnapshot context, CancellationToken cancellationToken = default)
            {
                if (ThrowError) throw new Exception("Simulated Engine Error");
                return _engine.SearchAsync(query, context, cancellationToken);
            }
        }
    }
}
