using System.Globalization;
using System.Resources;

namespace PowerTrove.Pack.Markdown;

internal static class SR
{
    private static readonly ResourceManager Manager = new("PowerTrove.Pack.Markdown.Resources.Strings", typeof(SR).Assembly);
    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? throw new MissingManifestResourceException(key);

    /// <summary>A <c>stem_One/_Other</c> family formatted with the count; Polish adds <c>_Few</c> and <c>_Many</c>.</summary>
    public static string Count(string stem, int count) => string.Format(CultureInfo.CurrentCulture,
        PowerTrove.Abstractions.Localization.PluralForms.Text(stem, count, key => Manager.GetString(key, CultureInfo.CurrentUICulture)), count);
}
