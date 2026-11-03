using System;
using System.Globalization;
using PowerTrove.Application.Localization;
using Xunit;

namespace PowerTrove.Application.Tests.Localization;

public sealed class BrowserExtensionSetupTests
{
    [Theory]
    [InlineData("de", "de")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pl", "pl")]
    [InlineData("ja", "ja")]
    [InlineData("en", "en")]
    public void UnpublishedStoreListingUsesTranslatedWebsiteGuide(string culture, string segment)
    {
        Assert.Equal($"https://www.powertrove.net/{segment}/download#browser-extension",
            BrowserExtensionSetup.InstallUri("chrome", CultureInfo.GetCultureInfo(culture)).AbsoluteUri);
        Assert.Equal($"https://www.powertrove.net/{segment}/download#browser-extension",
            BrowserExtensionSetup.InstallUri("edge", CultureInfo.GetCultureInfo(culture)).AbsoluteUri);
    }

    [Fact]
    public void PublishedListingUsesBrowserSpecificStore()
    {
        var culture = CultureInfo.GetCultureInfo("de");
        Assert.Equal("https://chromewebstore.google.com/detail/chrome-test",
            BrowserExtensionSetup.SelectInstallUri("chrome", culture, "chrome-test", "edge-test").AbsoluteUri);
        Assert.Equal("https://microsoftedge.microsoft.com/addons/detail/edge-test",
            BrowserExtensionSetup.SelectInstallUri("edge", culture, "chrome-test", "edge-test").AbsoluteUri);
    }

    [Fact]
    public void InvalidBrowserFailsClosed() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BrowserExtensionSetup.InstallUri("firefox", CultureInfo.GetCultureInfo("en")));
}
