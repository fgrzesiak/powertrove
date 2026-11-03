using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PowerTrove.Application.Localization;

/// <summary>
/// The display languages PowerTrove ships and how the user's choice maps to a UI culture
/// (ADR 0009). English is the neutral resource language and the fallback for everything else.
/// </summary>
public static class UiLanguages
{
    /// <summary>Setting value that follows the Windows display language.</summary>
    public const string System = "system";

    public const string English = "en";

    public const string German = "de";
    public const string Spanish = "es";
    public const string BrazilianPortuguese = "pt-BR";
    public const string French = "fr";
    public const string Japanese = "ja";
    public const string Polish = "pl";
    public const string Italian = "it";
    public const string Turkish = "tr";
    public const string Indonesian = "id";

    /// <summary>Canonical BCP 47 codes; all resources are required by the architecture tests.</summary>
    public static IReadOnlyList<string> Supported { get; } =
    [
        English, German, Spanish, BrazilianPortuguese, French, Japanese,
        Polish, Italian, Turkish, Indonesian
    ];

    /// <summary>
    /// The language's own name for a language picker, capitalized like Windows does there:
    /// Windows returns "español" or "polski", which those languages only use inside sentences.
    /// </summary>
    public static string DisplayName(string code)
    {
        var culture = CultureInfo.GetCultureInfo(code);
        string name = culture.NativeName;
        return name.Length == 0 ? name : culture.TextInfo.ToUpper(name[0]) + name[1..];
    }

    /// <summary>Returns the setting value to store: a supported code, or null for "follow Windows".</summary>
    public static string? Normalize(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
            return null;
        string value = setting.Trim();
        return Supported.FirstOrDefault(language =>
            string.Equals(language, value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the UI culture: an explicit supported language wins; otherwise the first
    /// supported language in the system UI culture's parent chain (de-AT -> de); otherwise English.
    /// </summary>
    public static CultureInfo Resolve(string? setting, CultureInfo systemUiCulture)
    {
        ArgumentNullException.ThrowIfNull(systemUiCulture);
        string? explicitLanguage = Normalize(setting);
        if (explicitLanguage != null)
            return CultureInfo.GetCultureInfo(explicitLanguage);

        for (var culture = systemUiCulture; !Equals(culture, CultureInfo.InvariantCulture); culture = culture.Parent)
        {
            string language = culture.TwoLetterISOLanguageName;
            string? match = Supported.FirstOrDefault(code =>
                string.Equals(code, culture.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, language, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return CultureInfo.GetCultureInfo(match);
            // Translate general Portuguese Windows installations using pt-BR resources.
            if (language == "pt")
                return CultureInfo.GetCultureInfo(BrazilianPortuguese);
        }
        return CultureInfo.GetCultureInfo(English);
    }

    /// <summary>
    /// Makes <paramref name="uiCulture"/> the UI culture of the process: resource lookups in all
    /// assemblies (Application, Desktop, SDK, packs) follow CultureInfo.CurrentUICulture.
    /// Number and date formatting keep the user's regional format (CurrentCulture is unchanged).
    /// </summary>
    public static void Apply(CultureInfo uiCulture)
    {
        ArgumentNullException.ThrowIfNull(uiCulture);
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
        CultureInfo.CurrentUICulture = uiCulture;
    }
}
