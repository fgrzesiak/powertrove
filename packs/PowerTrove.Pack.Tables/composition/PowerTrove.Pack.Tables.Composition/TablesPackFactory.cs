using PowerTrove.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(PowerTrove.Pack.Tables.TablesPackFactory))]

namespace PowerTrove.Pack.Tables;

public sealed class TablesPackFactory : IPackFactory
{
    public ActionPackInfo Info => TablesPack.Describe();
    public IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new TablesPack(host.Files.Planner, host.Files.FileSystem, host.Files);
    }
}
