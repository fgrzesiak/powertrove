using System.Text.Json;
namespace PowerTrove.Architecture.Tests;

public sealed class WebsiteCatalogLocalesTests
{
    [Fact]
    public void ActionCatalog_ListsExactlyTheDesktopUiLanguages()
    {
        // The website renders action texts in each of these languages.
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackFactoriesTests.FindRoot(), "docs/catalog/actions.json")));
        Assert.Equal(PowerTrove.Application.Localization.UiLanguages.Supported,
            catalog.RootElement.GetProperty("locales").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public void ActionCatalog_FitsTheWebsiteAndDesktopSizeLimit()
    {
        // contextbar-web's maxCatalogBytes equals this bound; a larger file takes every tool page offline.
        long size = new FileInfo(Path.Combine(PackFactoriesTests.FindRoot(), "docs/catalog/actions.json")).Length;
        Assert.True(size <= PowerTrove.Core.Catalog.ActionCatalog.MaximumJsonBytes,
            $"actions.json has {size} bytes; the limit is {PowerTrove.Core.Catalog.ActionCatalog.MaximumJsonBytes}.");
    }

    [Fact]
    public void ActionCatalog_SearchTermsAreCompleteAndUniquePerLanguage()
    {
        // The website reader rejects the whole catalog when one list repeats a term (case-insensitive).
        string root = PackFactoriesTests.FindRoot();
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs/catalog/actions.json")));
        var locales = catalog.RootElement.GetProperty("locales").EnumerateArray().Select(v => v.GetString()!).ToArray();
        var problems = new List<string>();
        foreach (var action in catalog.RootElement.GetProperty("packs").EnumerateArray()
            .SelectMany(pack => pack.GetProperty("actions").EnumerateArray()))
        {
            string slug = action.GetProperty("slug").GetString()!;
            var terms = action.GetProperty("searchTerms");
            foreach (string locale in locales)
            {
                if (!terms.TryGetProperty(locale, out var list)) { problems.Add($"{slug}/{locale}: missing"); continue; }
                var values = list.EnumerateArray().Select(v => v.GetString()!).ToArray();
                if (values.Length > 64) problems.Add($"{slug}/{locale}: more than 64 terms");
                if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
                    problems.Add($"{slug}/{locale}: duplicate term");
            }
        }
        Assert.Empty(problems);
    }
}
