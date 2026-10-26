using ContextBar.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(ContextBar.Pack.Data.DataPackFactory))]

namespace ContextBar.Pack.Data;

public sealed class DataPackFactory : IPackFactory
{
    public ActionPackInfo Info => DataPack.Describe();
    public IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new DataPack(host.Files);
    }
}
