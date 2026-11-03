using PowerTrove.Abstractions.Localization;
using System.Globalization;
using System.Resources;

namespace PowerTrove.Pack.Images;

internal static class SR
{
    private static readonly ResourceManager ResourceManager = new ResourceManager("PowerTrove.Pack.Images.Resources.Strings", typeof(SR).Assembly);

    public static string GetString(string name)
    {
        return ResourceManager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
    }
    public static string Plural(string stem, int count) =>
        GetString(PluralForms.ResourceKey(stem, count, CultureInfo.CurrentUICulture,
            key => GetString(key) != key, "_"));

    /// <summary>"{count} {noun}"; Polish adds plural forms as <c>pluralKey + "Few"</c> and <c>"Many"</c>.</summary>
    public static string Noun(int count, string singularKey, string pluralKey) =>
        PluralForms.Noun(count, singularKey, pluralKey, key => ResourceManager.GetString(key, CultureInfo.CurrentUICulture));
}
