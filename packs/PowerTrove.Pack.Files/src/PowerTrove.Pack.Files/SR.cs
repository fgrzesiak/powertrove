using PowerTrove.Abstractions.Localization;
using System.Globalization;
using System.Resources;

namespace PowerTrove.Pack.Files;

internal static class SR
{
    private static readonly ResourceManager ResourceManager = new ResourceManager("PowerTrove.Pack.Files.Resources.Strings", typeof(SR).Assembly);

    public static string GetString(string name)
    {
        return ResourceManager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
    }

    public static string Format(string name, params object?[] values) => string.Format(CultureInfo.CurrentCulture, GetString(name), values);
    public static string Count(string name, int count) => string.Format(CultureInfo.CurrentCulture, Plural(name, count), count);
    public static string Plural(string stem, int count) =>
        GetString(PluralForms.ResourceKey(stem, count, CultureInfo.CurrentUICulture,
            key => GetString(key) != key, "_"));

    /// <summary>"{count} {noun}"; Polish adds plural forms as <c>pluralKey + "Few"</c> and <c>"Many"</c>.</summary>
    public static string Noun(int count, string singularKey, string pluralKey) =>
        PluralForms.Noun(count, singularKey, pluralKey, key => ResourceManager.GetString(key, CultureInfo.CurrentUICulture));
}
