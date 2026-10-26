using PowerTrove.Pack.Office.Windows;
using PowerTrove.Abstractions.Office;
using PowerTrove.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(PowerTrove.Pack.Office.OfficePackFactory))]

namespace PowerTrove.Pack.Office;

public sealed class OfficePackFactory : IPackFactory
{
    public static IOfficeConverter CreateConverter(IOfficeInputValidator validator) => new OfficeComConverter(validator);
    public ActionPackInfo Info => OfficePack.Describe();
    public IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new OfficePack(host.Files, host.GetService<IOfficeConverter>() ?? (host.GetService<IOfficeInputValidator>() is { } validator ? CreateConverter(validator) : null));
    }
}
