using ContextBar.Abstractions;
using ContextBar.Application.ViewModels;
using ContextBar.Core;
using ContextBar.Core.Search;
using Xunit;

namespace ContextBar.Application.Tests;

public sealed class PaletteUndoTests
{
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task UndoReceiptIsAvailableOnceAndUpdatesResult(bool success)
    {
        var action = new UndoAction(); var registry = new ActionRegistry(); registry.Register(action);
        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available,
            new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);
        var undo = new FakeUndo(success); var vm = new PaletteViewModel { UndoService = undo };
        vm.Initialize(snapshot, registry, new ActionSearchEngine(registry)); await vm.SearchTask;
        await vm.ExecuteSelectedActionAsync(); Assert.True(vm.CanUndoResult); Assert.Empty(vm.CurrentOutputPaths);
        await vm.UndoResultAsync(); Assert.Equal(1, undo.Calls); Assert.Equal(!success, vm.CanUndoResult); Assert.Equal(success, vm.ExecutionIsSuccess);
        if (success) { await vm.UndoResultAsync(); Assert.Equal(1, undo.Calls); }
    }
    private sealed class UndoAction : IAction
    {
        public ActionDescriptor Descriptor => new("test.rename", "Rename", "", new HashSet<ContentKind> { ContentKind.None }, [], true, true);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot c, CancellationToken ct) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot c, ActionParameters p, CancellationToken ct) => new(new ActionPreview(true, null, [], []));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot c, ActionParameters p, IProgress<ActionProgress>? progress, CancellationToken ct) =>
            Task.FromResult(new ActionReceipt(true, false, [], [], true, UndoOperationId: Guid.NewGuid(), UndoUntil: DateTimeOffset.UtcNow.AddMinutes(10)));
    }
    private sealed class FakeUndo(bool success) : IUndoService
    {
        public int Calls { get; private set; }
        public Task<UndoResult> UndoAsync(Guid id, CancellationToken ct) { Calls++; return Task.FromResult(new UndoResult(success, [])); }
        public Task<bool> CanUndoAsync(Guid id, CancellationToken ct) => Task.FromResult(true);
        public IReadOnlyList<OperationReceipt> GetHistory() => [];
        public Task<OperationReceipt> RecordOperationAsync(Guid id, string action, IReadOnlyList<string> paths, CancellationToken ct) => throw new NotSupportedException();
    }
}
