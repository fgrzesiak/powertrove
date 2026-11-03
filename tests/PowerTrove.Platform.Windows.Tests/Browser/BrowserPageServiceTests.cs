using System.Text.Json;
using PowerTrove.Abstractions;
using PowerTrove.Platform.Windows.Browser;

namespace PowerTrove.Platform.Windows.Tests.Browser;

public class BrowserPageServiceTests
{
    private static BrowserPageInfo Page() => new("https://example.org", "Docs", "example.org",
        true, 4, true, 1, 2, 2, 0, 0, "en", 10)
    { Tab = new BrowserTabHandle(Guid.NewGuid(), 1, 2, "document-1") };

    [Fact]
    public async Task TypedTableResultCanFeedExistingTableActions()
    {
        var service = new BrowserPageService((_,operation,_) => Task.FromResult<JsonElement?>(
            JsonDocument.Parse("[{\"data\":[[\"Name\",\"Value\"],[\"A\",\"1\"]]}]")
                .RootElement.Clone()));
        var value = await service.FetchAsync(Page(), BrowserPageOperation.Tables, default);
        var table = Assert.Single(value!.Tables!);
        var data = table.ToTableData();
        Assert.Equal("A",Assert.IsType<TextCell>(data.Rows[1][0]).Value);
    }

    [Fact]
    public async Task InvalidNestedDataIsRejected()
    {
        var service = new BrowserPageService((_,_,_)=>Task.FromResult<JsonElement?>(
            JsonDocument.Parse("[{\"data\":[[\"hello\",7]]}]").RootElement.Clone()));
        Assert.Null(await service.FetchAsync(Page(),BrowserPageOperation.Tables,default));
    }

    [Fact]
    public async Task AMissingTabNeverCallsTheBrowser()
    {
        bool called = false;
        var service = new BrowserPageService((_,_,_)=>{called=true;throw new Exception("Unsafe call");});
        var withoutTab=Page() with {Tab=null};
        Assert.Null(await service.FetchAsync(withoutTab,BrowserPageOperation.ArticleMarkdown,default));
        Assert.False(called);
    }

    [Fact]
    public async Task UntrustedLinkSchemesAreRejected()
    {
        var service = new BrowserPageService((_,_,_)=>Task.FromResult<JsonElement?>(
            JsonDocument.Parse("[{\"title\":\"click\",\"url\":\"javascript:alert(1)\"}]")
                .RootElement.Clone()));
        Assert.Null(await service.FetchAsync(Page(),BrowserPageOperation.Links,default));
    }
    [Fact]
    public async Task BinaryCaptureRequiresAuthorizedTabAndEnforcesSizeBound()
    {
        string? requested = null;
        byte[] png = [137,80,78,71,13,10,26,10,1];
        var service = new BrowserPageService((_,_,_)=>throw new Exception("Wrong API"),
            (_,operation,_) => {
                requested = operation;
                return Task.FromResult<byte[]?>(png);
            });
        var result = await service.FetchAsync(Page(), BrowserPageOperation.CaptureVisiblePng, default);
        Assert.Equal("captureVisible", requested);
        Assert.Equal(png, result!.Binary);
        Assert.Null(await service.FetchAsync(Page() with {Tab=null},
            BrowserPageOperation.CaptureVisiblePng, default));
        var oversized = new BrowserPageService((_,_,_)=>throw new Exception("Wrong API"),
            (_,_,_)=> Task.FromResult<byte[]?>(new byte[24*1024*1024+1]));
        Assert.Null(await oversized.FetchAsync(Page(), BrowserPageOperation.CaptureFullPng, default));
    }

}
