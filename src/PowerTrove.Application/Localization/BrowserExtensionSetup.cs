using System;
using System.Globalization;

namespace PowerTrove.Application.Localization;

/// <summary>Official extension listing ids and the single website fallback for unpublished listings.</summary>
public static class BrowserExtensionSetup
{
    public const string PackId = "powertrove.browser";
    public const string? ChromeWebStoreId = null;
    public const string? EdgeAddonsId = null;
    public static bool IsPreview => ChromeWebStoreId is null || EdgeAddonsId is null;

    public static Uri InstallUri(string browser, CultureInfo uiCulture) =>
        SelectInstallUri(browser, uiCulture, ChromeWebStoreId, EdgeAddonsId);

    // Separate pure selector allows testing future store IDs without publishing a real listing.
    public static Uri SelectInstallUri(string browser, CultureInfo uiCulture, string? chromeId, string? edgeId)
    {
        ArgumentNullException.ThrowIfNull(uiCulture);
        string? storeId = browser switch
        {
            "chrome" => chromeId,
            "edge" => edgeId,
            _ => throw new ArgumentOutOfRangeException(nameof(browser))
        };
        if (!string.IsNullOrWhiteSpace(storeId))
            return new Uri(browser == "chrome"
                ? $"https://chromewebstore.google.com/detail/{Uri.EscapeDataString(storeId)}"
                : $"https://microsoftedge.microsoft.com/addons/detail/{Uri.EscapeDataString(storeId)}");

        var language = UiLanguages.Resolve(uiCulture.Name, uiCulture).Name;
        return new Uri($"https://www.powertrove.net/{language}/download#browser-extension");
    }
}
