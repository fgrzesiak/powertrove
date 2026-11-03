using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(PowerTrove.Pack.Browser.BrowserPackFactory))]

namespace PowerTrove.Pack.Browser;

public sealed class BrowserPackFactory : IPackFactory
{
    public ActionPackInfo Info => new BrowserPack().Info;
    public IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new BrowserPack(host.GetService<IBrowserPageService>(),
            host.Files.FileSystem, host.Files.Planner);
    }
}
