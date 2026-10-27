using System.Globalization;
using System.Resources;
namespace PowerTrove.Pack.EInvoice;
internal static class SR
{
    private static readonly ResourceManager Manager = new("PowerTrove.Pack.EInvoice.Resources.Strings", typeof(SR).Assembly);
    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? throw new MissingManifestResourceException(key);
}
