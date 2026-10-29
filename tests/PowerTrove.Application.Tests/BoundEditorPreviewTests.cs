using PowerTrove.Abstractions.Testing;
using PowerTrove.Application.ViewModels;
using PowerTrove.Core;
using PowerTrove.Core.Search;

namespace PowerTrove.Application.Tests;

public sealed class BoundEditorPreviewTests
{
    [Fact]
    public async Task BackgroundSearchCannotRetargetAnExplicitEditorPreview()
    {
        var edit = new PreviewAction("editor");
        var other = new PreviewAction("other");
        var registry = new ActionRegistry(); registry.Register(edit); registry.Register(other);
        var snapshot = Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "generated.png"));
        var vm = new PaletteViewModel(); vm.Initialize(snapshot, registry, new ActionSearchEngine(registry)); await vm.SearchTask;
        vm.SelectedAction = new(edit, true, null, 0);
        vm.SearchText = "other"; await vm.SearchTask;
        Assert.Same(other, vm.SelectedAction!.Action);
        var preview = await vm.PreviewSelectedActionAsync(new EmptyParameters(), expectedSnapshot: snapshot, expectedAction: edit);
        Assert.False(preview!.IsBlocked); Assert.Equal(1, edit.Previews); Assert.Equal(0, other.Previews);
        Assert.Same(other, vm.SelectedAction.Action);
        vm.SelectedAction = null;
        Assert.False((await vm.PreviewSelectedActionAsync(new EmptyParameters(), expectedSnapshot: snapshot, expectedAction: edit))!.IsBlocked);
        Assert.Equal(2, edit.Previews);
    }

    [Fact]
    public async Task BoundPreviewStillRejectsExpiredReplacedAndRetiredInputs()
    {
        var edit = new PreviewAction("editor"); var registry = new ActionRegistry(); registry.RegisterPack("fixture", [edit]);
        var snapshot = Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "generated.png"));
        var vm = new PaletteViewModel(); var engine = new ActionSearchEngine(registry);
        var expired = snapshot.Stale(); vm.Initialize(expired, registry, engine); await vm.SearchTask;
        Assert.True((await vm.PreviewSelectedActionAsync(new EmptyParameters(), expectedSnapshot: expired, expectedAction: edit))!.IsBlocked);
        vm.Initialize(snapshot, registry, engine); await vm.SearchTask;
        Assert.True((await vm.PreviewSelectedActionAsync(new EmptyParameters(), expectedSnapshot: snapshot with { InvocationId = Guid.NewGuid() }, expectedAction: edit))!.IsBlocked);
        registry.RegisterPack("fixture", [new PreviewAction("editor")]);
        Assert.True((await vm.PreviewSelectedActionAsync(new EmptyParameters(), expectedSnapshot: snapshot, expectedAction: edit))!.IsBlocked);
        Assert.Equal(0, edit.Previews);
    }

    private sealed class PreviewAction(string name) : IAction
    {
        public int Previews { get; private set; }
        public ActionDescriptor Descriptor { get; } = new("fixture." + name, name, name, new HashSet<ContentKind> { ContentKind.Files }, [name], true, true);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken ct) => ValueTask.FromResult(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
        { Previews++; return ValueTask.FromResult(new ActionPreview(true, "Generated test preview", [], [])); }
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken ct) => throw new InvalidOperationException();
    }
}
