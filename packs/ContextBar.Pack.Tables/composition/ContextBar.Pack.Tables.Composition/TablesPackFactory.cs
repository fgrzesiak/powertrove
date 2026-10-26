using ContextBar.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(ContextBar.Pack.Tables.TablesPackFactory))]

namespace ContextBar.Pack.Tables;

public sealed class TablesPackFactory : IPackFactory
{
    public ActionPackInfo Info => TablesPack.Describe();
    public IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new TablesPack(host.Files.Planner, host.Files.FileSystem, host.Files);
    }
}
