using PowerTrove.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(PowerTrove.Pack.Text.TextPackFactory))]

namespace PowerTrove.Pack.Text;

public sealed class TextPackFactory : IPackFactory
{
    public ActionPackInfo Info => TextPack.Describe();
    public IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new TextPack();
    }
}
