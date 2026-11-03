using PowerTrove.Pack.Browser;

namespace PowerTrove.Pack.Browser.Tests;

public class BrowserDataActionTests
{
    private sealed class FakePageService : IBrowserPageService
    {
        public Task<BrowserPageContent?> FetchAsync(BrowserPageInfo page,
            BrowserPageOperation operation,CancellationToken token)
        {
            var content = operation switch
            {
                BrowserPageOperation.ArticleMarkdown => new BrowserPageContent(operation,Markdown:"# Page\n\nContent"),
                BrowserPageOperation.Links => new BrowserPageContent(operation,Links:[
                    new BrowserLink("Docs","https://example.org/docs")]),
                BrowserPageOperation.Tables => new BrowserPageContent(operation,Tables:[
                    new BrowserTable([[ "Name","Formula" ],[ "A","=SUM(1,2)" ]])]),
                _ => new BrowserPageContent(operation,Markdown:"Data")
            };
            return Task.FromResult<BrowserPageContent?>(content);
        }
    }

    private static ContextSnapshot Snapshot(bool authorized=true)
    {
        var now=DateTimeOffset.UtcNow;
        var page=new BrowserPageInfo("https://example.org/","Docs","example.org",true,8,
            true,1,2,2,0,0,"en",10)
        {
            Tab=authorized?new BrowserTabHandle(Guid.NewGuid(),1,2,"document-a"):null
        };
        return new ContextSnapshot(Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero,0,"chrome",now,"Docs"),
            ContextStatus.Available,new TextSelection("Selected"),now,now.AddMinutes(2),
            "powertrove.provider.browser.explicit","https://example.org/")
        {BrowserPage=page};
    }

    [Fact]
    public async Task OnDemandBrowserActionsNeedAuthorizedTab()
    {
        var action=new BrowserDataAction("article-markdown",new FakePageService());
        Assert.False((await action.MatchAsync(Snapshot(authorized:false),default)).IsAvailable);
        Assert.True((await action.MatchAsync(Snapshot(),default)).IsAvailable);
        var receipt=await action.ExecuteAsync(Snapshot(),new EmptyParameters(),null,default);
        Assert.Equal("# Page\n\nContent",receipt.ClipboardText);
        Assert.Empty(receipt.OutputPaths);
    }

    [Fact]
    public async Task TableCsvPreventsSpreadsheetFormulaExecution()
    {
        var action=new BrowserDataAction("table-csv",new FakePageService());
        var receipt=await action.ExecuteAsync(Snapshot(),new EmptyParameters(),null,default);
        Assert.True(receipt.IsSuccess);
        Assert.Contains("'=SUM(1,2)",receipt.ClipboardText);
    }
}
