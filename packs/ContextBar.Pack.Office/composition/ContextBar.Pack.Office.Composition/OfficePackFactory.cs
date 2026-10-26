using ContextBar.Pack.Office.Windows;
using ContextBar.Abstractions.Office;
using ContextBar.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(ContextBar.Pack.Office.OfficePackFactory))]

namespace ContextBar.Pack.Office;

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
