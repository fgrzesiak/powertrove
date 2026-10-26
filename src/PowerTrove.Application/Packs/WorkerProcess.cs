namespace PowerTrove.Application.Packs;

/// <summary>OS process/job ownership is implemented by Platform.Windows, never by Application.</summary>
public interface IPackWorkerProcess : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    Task KillAsync(CancellationToken cancellationToken = default);
}

public interface IPackWorkerProcessFactory
{
    Task<IPackWorkerProcess> StartAsync(PackWorkerGroup group, string workDirectory, CancellationToken cancellationToken);
}

public sealed record PackWorkerIdentity(string Id, string Version);
public sealed record PackWorkerGroup(string Id, IReadOnlyList<string> PackDirectories, IReadOnlyList<PackWorkerIdentity> Packs,
    IReadOnlyList<string>? ManifestHashes = null);
