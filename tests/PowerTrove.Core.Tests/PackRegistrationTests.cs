using PowerTrove.Abstractions;

namespace PowerTrove.Core.Tests;

public sealed class PackRegistrationTests
{
    [Fact]
    public void PackReplacementPublishesANewSnapshotAndRemovesObsoleteActions()
    {
        var registry = new ActionRegistry();
        var old = new StubAction("contextbar.fixture.old");
        var replacement = new StubAction("contextbar.fixture.new");
        registry.RegisterPack("contextbar.fixture", [old]);
        var snapshot = registry.GetAllActions().ToArray();
        registry.RegisterPack("contextbar.fixture", [replacement]);
        Assert.Same(old, Assert.Single(snapshot));
        Assert.Null(registry.GetAction(old.Descriptor.Id));
        Assert.Same(replacement, Assert.Single(registry.GetAllActions()));
    }

    [Fact]
    public void CollisionOrDuplicateCannotPartiallyReplaceAnExistingPack()
    {
        var registry = new ActionRegistry();
        var original = new StubAction("contextbar.fixture.copy");
        var command = new StubAction("contextbar.app.command");
        registry.RegisterPack("contextbar.fixture", [original]);
        registry.Register(command);
        Assert.Throws<InvalidOperationException>(() => registry.RegisterPack("contextbar.fixture", [new StubAction("contextbar.fixture.new"), command]));
        Assert.Same(original, registry.GetAction(original.Descriptor.Id));
        Assert.Same(command, registry.GetAction(command.Descriptor.Id));
        Assert.Null(registry.GetAction("contextbar.fixture.new"));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterPack("contextbar.other", [original]));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterPack("contextbar.fixture", [original, original]));
        Assert.Same(original, registry.GetAction(original.Descriptor.Id));
    }
    private sealed class StubAction(string id) : IAction
    {
        public ActionDescriptor Descriptor { get; } = new(id, "Generated fixture", "Generated fixture", new HashSet<ContentKind> { ContentKind.Files }, [], true, true);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) =>
            new(new ActionPreview(true, null, [], []));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new ActionReceipt(true, false, [], [], false));
    }
}
