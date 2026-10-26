using System;
using System.Globalization;
using System.IO;
using ContextBar.Application.Configuration;
using ContextBar.Application.Localization;
using Xunit;

namespace ContextBar.Application.Tests.Localization;

public class UiLanguagesTests
{
    [Theory]
    [InlineData(null, "de-DE", "de")]
    [InlineData("system", "de-AT", "de")]
    [InlineData(null, "en-GB", "en")]
    [InlineData(null, "fr-FR", "en")]
    [InlineData(null, "", "en")]
    [InlineData("en", "de-DE", "en")]
    [InlineData("de", "en-US", "de")]
    [InlineData("DE", "en-US", "de")]
    [InlineData("xx", "de-DE", "de")]
    public void Resolve_PrefersExplicitChoiceThenWindowsThenEnglish(string? setting, string system, string expected)
    {
        var culture = UiLanguages.Resolve(setting, CultureInfo.GetCultureInfo(system));

        Assert.Equal(expected, culture.Name);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("system", null)]
    [InlineData(" de ", "de")]
    [InlineData("EN", "en")]
    [InlineData("fr", null)]
    public void Normalize_KeepsOnlySupportedLanguages(string? input, string? expected)
    {
        Assert.Equal(expected, UiLanguages.Normalize(input));
    }

    [Fact]
    public void Apply_SetsUiCultureButKeepsRegionalFormat()
    {
        var originalUi = CultureInfo.CurrentUICulture;
        var originalDefault = CultureInfo.DefaultThreadCurrentUICulture;
        var originalFormat = CultureInfo.CurrentCulture;
        try
        {
            UiLanguages.Apply(CultureInfo.GetCultureInfo("de"));

            Assert.Equal("de", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("de", CultureInfo.DefaultThreadCurrentUICulture?.Name);
            Assert.Equal(originalFormat, CultureInfo.CurrentCulture);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUi;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefault;
        }
    }

    [Fact]
    public void SettingsService_PersistsLanguageAndDropsUnsupportedValues()
    {
        string path = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid()}.json");
        try
        {
            var service = new SettingsService(path);
            Assert.Null(service.Settings.UiLanguage);

            service.SetUiLanguage("de");
            Assert.Equal("de", new SettingsService(path).Settings.UiLanguage);

            service.SetUiLanguage("klingon");
            Assert.Null(new SettingsService(path).Settings.UiLanguage);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }

    [Fact]
    public void SettingsService_LoadsFilesWithoutLanguageAsFollowWindows()
    {
        string path = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """{"Version":3,"PinnedActions":[],"RecentActions":{},"ActionPreferences":{}}""");
            var service = new SettingsService(path);

            Assert.Null(service.Settings.UiLanguage);
            Assert.Equal(UserSettings.CurrentVersion, service.Settings.Version);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }
}
