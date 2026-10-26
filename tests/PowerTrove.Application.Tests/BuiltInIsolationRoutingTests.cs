using PowerTrove.Abstractions;
using PowerTrove.Application.Packs;
using Xunit;

namespace PowerTrove.Application.Tests;

public sealed class BuiltInIsolationRoutingTests
{
    [Fact]
    public async Task MissingComposition_FailsClosedWithoutStartingWorkerOrDisablingFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "PowerTrove-Isolation-Routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "safe.txt"), "fixture");
            var factory = new NoProcessFactory();
            await using var supervisor = new PackHostSupervisor(factory, Path.Combine(root, "user"));
            var pdfAction = new StubAction();
            var filesAction = new StubAction();
            var builtIns = new IActionPack[]
            {
                new StubPack(new ActionPackInfo("contextbar.pdf", "PDF", "0.1.0", "PowerTrove"), [pdfAction]),
                new StubPack(new ActionPackInfo("contextbar.files", "Files", "0.1.0", "PowerTrove"), [filesAction])
            };
            var result = await BuiltInPackIsolation.RouteAsync(builtIns, supervisor, root);
            Assert.False(result.Ready);
            Assert.NotSame(pdfAction, result.Packs[0].Actions[0]);
            Assert.Same(filesAction, result.Packs[1].Actions[0]);
            Assert.False(factory.Started);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed record StubPack(ActionPackInfo Info, IReadOnlyList<IAction> Actions) : IActionPack;
    private sealed class StubAction : IAction
    {
        public ActionDescriptor Descriptor { get; } = new("contextbar.test.fixture", "fixture", "test", new HashSet<ContentKind> { ContentKind.Files },
            ["fixture"], true, true);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken token) => throw new NotImplementedException();
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters values, CancellationToken token) => throw new NotImplementedException();
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters values, IProgress<ActionProgress>? progress, CancellationToken token) => throw new NotImplementedException();
    }
    private sealed class NoProcessFactory : IPackWorkerProcessFactory
    {
        public bool Started;
        public Task<IPackWorkerProcess> StartAsync(PackWorkerGroup definition, string work, CancellationToken token)
        { Started = true; throw new InvalidOperationException("Workers must be lazy."); }
    }
}
