using PowerTrove.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(PowerTrove.Pack.Data.DataPackFactory))]

namespace PowerTrove.Pack.Data;

public sealed class DataPackFactory : IPackFactory
{
    public ActionPackInfo Info => DataPack.Describe();
    public IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new DataPack(host.Files);
    }
}
