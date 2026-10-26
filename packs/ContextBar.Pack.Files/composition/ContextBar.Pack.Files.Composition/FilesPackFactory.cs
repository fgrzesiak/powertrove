using ContextBar.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(ContextBar.Pack.Files.FilesPackFactory))]

namespace ContextBar.Pack.Files;

public sealed class FilesPackFactory : IPackFactory
{
    public ActionPackInfo Info => FilesPack.Describe();
    public IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new FilesPack(host.Files, host.GetService<IDirectoryScanner>(), host.GetService<IFileRenameService>());
    }
}
