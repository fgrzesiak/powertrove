namespace ContextBar.Pack.Media.Tests;

public abstract class MediaToolContract : ActionContractTestsBase, IDisposable
{
    protected readonly TempDirectory Directory = new();
    protected static FileActionServices Services() => FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
    protected static IMediaInspector Inspector() => new ContractInspector();
    protected static CompositeMediaEngine Engines() => new([new ContractEngine()]);
    private sealed class ContractInspector : IMediaInspector
    {
        public bool IsAvailable => true;
        public Task<MediaDetails> InspectAsync(string path, CancellationToken ct) => Task.FromResult(new MediaDetails(TimeSpan.FromSeconds(1), HasAudio: true));
    }
    private sealed class ContractEngine : IMediaEngine
    {
        public string Name => "contract"; public bool IsAvailable => true; public bool Supports(MediaRequest request) => true;
        public Task ProcessAsync(string source, string destination, MediaRequest request, CancellationToken ct) => File.WriteAllTextAsync(destination, "output", ct);
    }
    public void Dispose() => Directory.Dispose();
}
public sealed class TrimContract : MediaToolContract
{
    protected override IAction CreateAction() => new MediaToolAction("trim", Engines(), Inspector(), Services());
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(Directory.Write("source.wav", "input"));
}
public sealed class InfoContract : MediaToolContract
{
    protected override IAction CreateAction() => new MediaInfoAction(Inspector(), Services());
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(Directory.Write("source.wav", "input"));
}
public sealed class JoinContract : MediaToolContract
{
    protected override IAction CreateAction() => new JoinMediaAction(Engines(), Inspector(), Services());
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(Directory.Write("a.wav", "input"), Directory.Write("b.wav", "input"));
}
