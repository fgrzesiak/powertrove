using System.Globalization;
using System.Resources;

namespace PowerTrove.Pack.Office;

internal static class SR
{
    private static readonly ResourceManager Manager = new("PowerTrove.Pack.Office.Resources.Strings", typeof(SR).Assembly);
    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? throw new MissingManifestResourceException(key);
}
