using System.Globalization;
using System.Resources;

namespace ContextBar.Pack.Office;

internal static class SR
{
    private static readonly ResourceManager Manager = new("ContextBar.Pack.Office.Resources.Strings", typeof(SR).Assembly);
    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? throw new MissingManifestResourceException(key);
}
