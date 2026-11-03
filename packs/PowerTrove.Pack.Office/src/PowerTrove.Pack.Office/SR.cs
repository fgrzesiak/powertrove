using PowerTrove.Abstractions.Localization;
using System.Globalization;
using System.Resources;

namespace PowerTrove.Pack.Office;

internal static class SR
{
    private static readonly ResourceManager Manager = new("PowerTrove.Pack.Office.Resources.Strings", typeof(SR).Assembly);
    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? throw new MissingManifestResourceException(key);
    public static string Plural(string stem, int count) =>
        Get(PluralForms.ResourceKey(stem, count, CultureInfo.CurrentUICulture,
            key => Manager.GetString(key, CultureInfo.CurrentUICulture) != null, "_"));
}
