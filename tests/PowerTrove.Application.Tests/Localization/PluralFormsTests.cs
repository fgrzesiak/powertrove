using System.Globalization;
using PowerTrove.Abstractions.Localization;
using Xunit;

namespace PowerTrove.Application.Tests.Localization;

public sealed class PluralFormsTests
{
    [Theory]
    [InlineData(0, "Many")]
    [InlineData(1, "One")]
    [InlineData(2, "Few")]
    [InlineData(4, "Few")]
    [InlineData(5, "Many")]
    [InlineData(12, "Many")]
    [InlineData(14, "Many")]
    [InlineData(21, "Many")]
    [InlineData(22, "Few")]
    [InlineData(25, "Many")]
    [InlineData(112, "Many")]
    public void PolishCategories(int count, string category) =>
        Assert.Equal(category, PluralForms.Category(count, CultureInfo.GetCultureInfo("pl")));

    [Theory]
    [InlineData("en", 1, "One")]
    [InlineData("de", 2, "Other")]
    [InlineData("ja", 1, "Other")]
    [InlineData("tr", 1, "Other")]
    [InlineData("id", 1, "Other")]
    [InlineData("fr", 0, "Other")]
    [InlineData("fr", 1, "One")]
    public void OtherShippedLocales(string culture, int count, string expected) =>
        Assert.Equal(expected, PluralForms.Category(count, CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void MissingCategoryFallsBackToOther() =>
        Assert.Equal("CountOther", PluralForms.ResourceKey("Count", 2, CultureInfo.GetCultureInfo("pl"),
            key => key == "CountOther"));

    [Fact]
    public void PolishTextsUseFewAndManyWhereTheyExist()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pl");
        try
        {
            var texts = new System.Collections.Generic.Dictionary<string, string>
            {
                ["File_One"] = "plik", ["File_Few"] = "pliki", ["File_Other"] = "plików",
                ["Page"] = "strona", ["Pages"] = "strony", ["PagesMany"] = "stron"
            };
            string? Lookup(string key) => texts.TryGetValue(key, out var value) ? value : null;
            Assert.Equal("plik", PluralForms.Text("File", 1, Lookup));
            Assert.Equal("pliki", PluralForms.Text("File", 22, Lookup));
            Assert.Equal("plików", PluralForms.Text("File", 12, Lookup));
            Assert.Equal("1 strona", PluralForms.Noun(1, "Page", "Pages", Lookup));
            Assert.Equal("3 strony", PluralForms.Noun(3, "Page", "Pages", Lookup));
            Assert.Equal("5 stron", PluralForms.Noun(5, "Page", "Pages", Lookup));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }
}
