using PowerTrove.Application.Updates;

namespace PowerTrove.Application.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("v2.0.0", "1.999.0", 1)]
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-1", "1.0.0-alpha", -1)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("1.0.0+build.1", "1.0.0+build.2", 0)]
    [InlineData("1.0.0.0", "1.0.0", 0)]
    [InlineData("1.0.0.2", "1.0.0.1", 1)]
    [InlineData("99999999999999999999.0.0", "9999999999999999999.0.0", 1)]
    public void Versions_UseNumericAndPrereleasePrecedence(string left, string right, int expected)
    {
        Assert.True(ReleaseVersion.TryParse(left, out var a)); Assert.True(ReleaseVersion.TryParse(right, out var b));
        Assert.Equal(expected, Math.Sign(a!.CompareTo(b))); Assert.Equal(-expected, Math.Sign(b!.CompareTo(a)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0+meta..data")]
    [InlineData("1.0.0+meta+data")]
    [InlineData("1.0.0/asset.exe")]
    [InlineData(" 1.0.0")]
    public void InvalidVersions_AreRejected(string version) => Assert.False(ReleaseVersion.TryParse(version, out _));

    [Theory]
    [InlineData("https://github.com/fgrzesiak/powertrove/releases/tag/v1.0.0", true)]
    [InlineData("https://github.com/fgrzesiak/powertrove/releases/tag/1.0.0-beta.1", true)]
    [InlineData("http://github.com/fgrzesiak/powertrove/releases/tag/v1.0.0", false)]
    [InlineData("https://github.com.evil.test/fgrzesiak/powertrove/releases/tag/v1.0.0", false)]
    [InlineData("https://github.com/other/powertrove/releases/tag/v1.0.0", false)]
    [InlineData("https://github.com/fgrzesiak/powertrove/releases/download/v1.0.0/setup.exe", false)]
    [InlineData("https://user@github.com/fgrzesiak/powertrove/releases/tag/v1.0.0", false)]
    [InlineData("https://github.com:8443/fgrzesiak/powertrove/releases/tag/v1.0.0", false)]
    [InlineData("https://github.com/fgrzesiak/powertrove/releases/tag/v1.0.0?redirect=evil", false)]
    [InlineData("https://github.com/fgrzesiak/powertrove/releases/tag/v1.0.0#asset", false)]
    [InlineData("https://github.com/fgrzesiak/powertrove/releases/tag/v1.0.0%2Fasset", false)]
    [InlineData("https://github.com/fgrzesiak/powertrove/releases/tag/../latest", false)]
    public void ReleasePageValidation_OnlyAcceptsThisRepositorysTagPages(string value, bool valid) =>
        Assert.Equal(valid, ReleaseSource.TryValidatePage(new Uri(value), out _));
}
