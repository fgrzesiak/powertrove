using System.Text.Json;
using PowerTrove.Platform.Windows.Browser;

namespace PowerTrove.Platform.Windows.Tests.Browser;

public class BrowserPageResultTests
{
    [Theory]
    [InlineData("PowerTrove docs - Google Chrome", "PowerTrove docs", true)]
    [InlineData("PowerTrove docs - Work - Microsoft Edge", "PowerTrove docs", true)]
    [InlineData("Other - Google Chrome", "PowerTrove docs", false)]
    [InlineData("PowerTrove docs malicious suffix", "PowerTrove docs", false)]
    [InlineData("", "PowerTrove docs", false)]
    public void WindowMatchingRequiresTabTitlePrefixAndRealSeparator(string caption,string title,bool expected) =>
        Assert.Equal(expected, BrowserTabContextProvider.MatchesWindowTitle(caption,title));

    [Fact]
    public void BrowserPageDataRejectsIncognitoAndUnsafeUrls()
    {
        const string text = """
          {"title":"Documentation","windowTitle":"Documentation","url":"https://example.org/",
           "selection":"test","focused":true,"incognito":false,"tabId":4,"windowId":9}
          """;
        using var json = JsonDocument.Parse(text);
        Assert.NotNull(BrowserPageResult.Read("chrome", json.RootElement));
        string incognito = text.Replace("\"incognito\":false", "\"incognito\":true");
        using var incognitoJson = JsonDocument.Parse(incognito);
        Assert.Null(BrowserPageResult.Read("chrome", incognitoJson.RootElement));
        string invalid = text.Replace("https://example.org/", "file:///private");
        using var invalidJson = JsonDocument.Parse(invalid);
        Assert.Null(BrowserPageResult.Read("edge", invalidJson.RootElement));
    }

    [Fact]
    public void BrowserSignalsAreBoundedAndParsedAsTypedValues()
    {
        using var data = JsonDocument.Parse(
            "{\"title\":\"Docs\",\"windowTitle\":\"Docs\",\"url\":\"https://example.org/\",\"selection\":\"selected\",\"focused\":true,\"incognito\":false,\"tabId\":1,\"windowId\":4,\"signals\":{\"isArticle\":true,\"tableCount\":3,\"largestTableRows\":100,\"largestTableColumns\":4,\"imageCount\":12,\"codeBlockCount\":8,\"wordCount\":1200,\"language\":\"en\"}}");
        var page = BrowserPageResult.Read("chrome", data.RootElement);
        Assert.NotNull(page);
        Assert.True(page.IsArticle);
        Assert.Equal(3, page.TableCount);
        Assert.Equal(100, page.LargestTableRows);
        Assert.Equal(12, page.ImageCount);
        Assert.Equal(1200, page.WordCount);
    }

    [Fact]
    public void PageMetadataNeverAppearsInDiagnostics()
    {
        using var json = JsonDocument.Parse(
            "{\"title\":\"SECRET-TITLE\",\"windowTitle\":\"SECRET-TITLE\",\"url\":\"https://example.org/\",\"selection\":\"SECRET-TEXT\",\"focused\":true,\"incognito\":false,\"tabId\":4,\"windowId\":9}");
        var result = BrowserPageResult.Read("chrome",json.RootElement);
        Assert.NotNull(result);
        Assert.DoesNotContain("SECRET",result.ToString());
    }
}
