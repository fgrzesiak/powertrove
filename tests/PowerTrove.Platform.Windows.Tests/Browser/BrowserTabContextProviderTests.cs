using PowerTrove.Platform.Windows.Browser;

namespace PowerTrove.Platform.Windows.Tests.Browser;

public class BrowserTabContextProviderTests
{
    [Theory]
    [InlineData("Release notes - Google Chrome", "Release notes")]
    [InlineData("Release notes - Work - Google Chrome", "Release notes")]
    [InlineData("Release notes - Personal - Microsoft​ Edge", "Release notes")]
    [InlineData("Release notes and 3 more pages - Personal - Microsoft​ Edge", "Release notes")]
    [InlineData("Release notes und 3 weitere Seiten - Persönlich – Microsoft​ Edge", "Release notes")]
    [InlineData("Release notes", "Release notes")]
    public void MatchesChromeAndEdgeCaptions(string caption, string tabTitle) =>
        Assert.True(BrowserTabContextProvider.MatchesWindowTitle(caption, tabTitle));

    [Theory]
    [InlineData("Release notes 2 - Google Chrome", "Release notes 2 draft")]
    [InlineData("Release notesX - Google Chrome", "Release notes")]
    [InlineData("release notes - Google Chrome", "Release notes")]
    [InlineData("Other page - Google Chrome", "Release notes")]
    [InlineData("", "Release notes")]
    [InlineData("Release notes - Google Chrome", "")]
    public void RejectsOtherTabs(string caption, string tabTitle) =>
        Assert.False(BrowserTabContextProvider.MatchesWindowTitle(caption, tabTitle));
}
