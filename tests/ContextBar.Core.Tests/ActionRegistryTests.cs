using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ContextBar.Core.Tests;

public class ActionRegistryTests
{
    private class MockAction : IAction
    {
        public ActionDescriptor Descriptor { get; }
        public bool MatchAsyncCalled { get; private set; }
        public bool IsAvailable { get; set; } = true;
        public bool ThrowOnMatch { get; set; }

        public MockAction(string id, params ContentKind[] accepts)
        {
            Descriptor = new ActionDescriptor(
                Id: id,
                Title: $"Test Action {id}",
                Description: "A mock action",
                Accepts: new HashSet<ContentKind>(accepts),
                SearchTerms: Array.Empty<string>(),
                RequiresPreview: false,
                ChangesFiles: false);
        }

        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
        {
            MatchAsyncCalled = true;
            if (ThrowOnMatch) throw new InvalidOperationException("Sensitive source content");
            return new ValueTask<ActionAvailability>(new ActionAvailability(IsAvailable));
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

    private ContextSnapshot CreateSnapshot(
        ContentKind kind, string providerId = "TestProvider",
        IReadOnlySet<string>? providedCapabilities = null)
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
            ProviderId: providerId,
            UserMessage: null,
            ProvidedCapabilities: providedCapabilities);
    }

    [Fact]
    public void Registry_SupportsRegistrationAndLookup()
    {
        var registry = new ActionRegistry();
        var action = new MockAction("test.action", ContentKind.None);

        registry.Register(action);

        var retrieved = registry.GetAction("test.action");
        Assert.NotNull(retrieved);
        Assert.Same(action, retrieved);

        var notFound = registry.GetAction("unknown.action");
        Assert.Null(notFound);
    }

    [Fact]
    public async Task GetAvailableActionsAsync_FiltersIncompatibleActions()
    {
        var registry = new ActionRegistry();
        var fileAction = new MockAction("file.action", ContentKind.Files);
        var tableAction = new MockAction("table.action", ContentKind.Table);
        var anyAction = new MockAction("any.action", ContentKind.None);

        registry.Register(fileAction);
        registry.Register(tableAction);
        registry.Register(anyAction);

        var tableContext = CreateSnapshot(ContentKind.Table);

        var availableActions = new List<IAction>();
        await foreach (var action in registry.GetAvailableActionsAsync(tableContext))
        {
            availableActions.Add(action);
        }

        Assert.Contains(tableAction, availableActions);
        Assert.Contains(anyAction, availableActions);
        Assert.DoesNotContain(fileAction, availableActions);

        Assert.True(tableAction.MatchAsyncCalled);
        Assert.True(anyAction.MatchAsyncCalled);
        Assert.False(fileAction.MatchAsyncCalled); // MatchAsync should be bypassed for incompatible kind
    }

    [Fact]
    public async Task GetAvailableActionsAsync_RespectsMatchAsyncResult()
    {
        var registry = new ActionRegistry();
        var action = new MockAction("test.action", ContentKind.Table)
        {
            IsAvailable = false
        };

        registry.Register(action);
        var context = CreateSnapshot(ContentKind.Table);

        var availableActions = new List<IAction>();
        await foreach (var a in registry.GetAvailableActionsAsync(context))
        {
            availableActions.Add(a);
        }

        Assert.Empty(availableActions);
        Assert.True(action.MatchAsyncCalled);
    }

    [Fact]
    public async Task GetAvailableActionsAsync_UnavailableContext_ReturnsNoActions()
    {
        var registry = new ActionRegistry();
        var action = new MockAction("test.action", ContentKind.Table);
        registry.Register(action);

        var context = CreateSnapshot(ContentKind.Table) with { Status = ContextStatus.Unsupported };

        var availableActions = new List<IAction>();
        await foreach (var a in registry.GetAvailableActionsAsync(context))
        {
            availableActions.Add(a);
        }

        Assert.Empty(availableActions);
        Assert.False(action.MatchAsyncCalled);
    }

    [Fact]
    public async Task GetAvailableActionsAsync_ReusesActionAcrossDifferentProviders()
    {
        var registry = new ActionRegistry();
        var tableAction = new MockAction("table.export", ContentKind.Table);
        registry.Register(tableAction);

        var excelContext = CreateSnapshot(ContentKind.Table, providerId: "ExcelProvider");
        var csvContext = CreateSnapshot(ContentKind.Table, providerId: "CsvGenericProvider");

        // Act & Assert for Excel provider
        var availableForExcel = new List<IAction>();
        await foreach (var action in registry.GetAvailableActionsAsync(excelContext))
        {
            availableForExcel.Add(action);
        }
        Assert.Single(availableForExcel);
        Assert.Same(tableAction, availableForExcel[0]);

        // Reset tracking
        var freshAction = new MockAction("table.export", ContentKind.Table);
        var freshRegistry = new ActionRegistry();
        freshRegistry.Register(freshAction);

        // Act & Assert for CSV provider
        var availableForCsv = new List<IAction>();
        await foreach (var action in freshRegistry.GetAvailableActionsAsync(csvContext))
        {
            availableForCsv.Add(action);
        }
        Assert.Single(availableForCsv);
        Assert.Same(freshAction, availableForCsv[0]);
    }
    [Fact]
    public async Task GetAvailableActionsAsync_RequiresAllVersionedCapabilities()
    {
        var registry = new ActionRegistry();
        var action = new MockAction("capabilities", ContentKind.Table);
        var required = new HashSet<string> { "table.export.v1", "table.format.v1" };
        registry.Register(new CapabilityAction(action, required));

        var missing = new List<IAction>();
        await foreach (var item in registry.GetAvailableActionsAsync(
            CreateSnapshot(ContentKind.Table, providedCapabilities:
                new HashSet<string> { "table.export.v1" })))
            missing.Add(item);

        Assert.Empty(missing);
        Assert.False(action.MatchAsyncCalled);

        var matched = new List<IAction>();
        await foreach (var item in registry.GetAvailableActionsAsync(
            CreateSnapshot(ContentKind.Table, providedCapabilities: required)))
            matched.Add(item);
        Assert.Single(matched);
        Assert.True(action.MatchAsyncCalled);
    }

    [Fact]
    public async Task GetAvailableActionsAsync_BrokenMatcher_DoesNotHideOtherActions()
    {
        var registry = new ActionRegistry();
        registry.Register(new MockAction("broken", ContentKind.None) { ThrowOnMatch = true });
        registry.Register(new MockAction("safe", ContentKind.None));
        var results = new List<IAction>();
        await foreach (var item in registry.GetAvailableActionsAsync(CreateSnapshot(ContentKind.None)))
            results.Add(item);
        Assert.Equal("safe", Assert.Single(results).Descriptor.Id);
    }

    private sealed class CapabilityAction : IAction
    {
        private readonly MockAction _inner;
        public CapabilityAction(MockAction inner, IReadOnlySet<string> caps)
        {
            _inner = inner;
            Descriptor = inner.Descriptor with { RequiredCapabilities = caps };
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

}
