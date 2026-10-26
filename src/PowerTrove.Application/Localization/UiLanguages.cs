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

    /// <summary>Supported languages in menu order; adding one also needs Strings.&lt;code&gt;.resx files.</summary>
    public static IReadOnlyList<string> Supported { get; } = new[] { English, German };

    /// <summary>Returns the setting value to store: a supported code, or null for "follow Windows".</summary>
    public static string? Normalize(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
            return null;
        string value = setting.Trim().ToLowerInvariant();
        return Supported.Contains(value) ? value : null;
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
            if (Supported.Contains(language))
                return CultureInfo.GetCultureInfo(language);
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
