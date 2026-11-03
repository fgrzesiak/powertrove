using PowerTrove.Abstractions.Files;
using PowerTrove.Pack.Browser;

namespace PowerTrove.Pack.Browser.Tests;

public class BrowserFileExportActionTests
{
    private sealed class FakePages : IBrowserPageService
    {
        public Task<BrowserPageContent?> FetchAsync(BrowserPageInfo page,
            BrowserPageOperation operation, CancellationToken token) =>
            Task.FromResult<BrowserPageContent?>(operation switch
            {
                BrowserPageOperation.ArticleMarkdown or BrowserPageOperation.PageMarkdown =>
                    new(operation,Markdown:"# Example\n\nContent"),
                BrowserPageOperation.Tables =>
                    new(operation,Tables:[new BrowserTable([[ "Name","Value" ],[ "One","=1+1" ]])]),
                BrowserPageOperation.CaptureVisiblePng or BrowserPageOperation.CaptureFullPng =>
                    new(operation,Binary:[137,80,78,71,13,10,26,10,1,2,3]),
                BrowserPageOperation.CaptureMhtml =>
                    new(operation,Binary:System.Text.Encoding.ASCII.GetBytes("MIME-Version: 1.0\r\n")),
                _ => new(operation,Links:[new BrowserLink("Docs","https://example.org")])
            });
    }

    [Theory]
    [InlineData("save-article-md", ".md")]
    [InlineData("save-links-csv", ".csv")]
    [InlineData("save-table-csv", ".csv")]
    [InlineData("capture-visible-png", ".png")]
    [InlineData("capture-full-png", ".png")]
    [InlineData("capture-page-mhtml", ".mhtml")]
    [InlineData("capture-page-pdf", ".pdf")]
    public async Task SavedBrowserOutputIsCollisionFreeAndNeverOverwrites(string mode,string ext)
    {
        string dir=Path.Combine(Path.GetTempPath(),"powertrove-browser-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var fs=new PhysicalOutputFileSystem();
            var action=new BrowserFileExportAction(mode,new FakePages(),fs,new OutputPlanner(fs),
                (archive, token) =>
                {
                    // The PDF is printed from the browser's MHTML, not from extracted text.
                    Assert.StartsWith("MIME-Version:", System.Text.Encoding.ASCII.GetString(archive));
                    return Task.FromResult(System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF"));
                });
            var now=DateTimeOffset.UtcNow;
            var context=new ContextSnapshot(Guid.NewGuid(),
                new SourceWindowIdentity(IntPtr.Zero,0,"chrome",now,"Example"),
                ContextStatus.Available,new TextSelection("Selected"),now,now.AddMinutes(2),
                "powertrove.provider.browser.explicit","https://example.org")
            {
                BrowserPage=new BrowserPageInfo("https://example.org","Example","example.org",
                    true,8,true,1,2,2,0,0,"en",10)
                {Tab=new BrowserTabHandle(Guid.NewGuid(),1,2,"doc")}
            };
            var parameters=new DictionaryParameters(new Dictionary<string,string> {["OutputDirectory"]=dir});
            var first=await action.ExecuteAsync(context,parameters,null,default);
            var second=await action.ExecuteAsync(context,parameters,null,default);
            Assert.True(first.IsSuccess);
            Assert.True(second.IsSuccess);
            Assert.NotEqual(first.OutputPaths[0],second.OutputPaths[0]);
            Assert.EndsWith(ext,first.OutputPaths[0],StringComparison.OrdinalIgnoreCase);
            // Named after the page, like a browser download, never after the internal mode.
            Assert.StartsWith("Example",Path.GetFileName(first.OutputPaths[0]));
            Assert.True(File.Exists(first.OutputPaths[0]));
            Assert.True(File.Exists(second.OutputPaths[0]));
            if (mode=="save-table-csv")
                Assert.Contains("'=1+1",await File.ReadAllTextAsync(first.OutputPaths[0]));
            if (mode=="capture-page-pdf")
                Assert.StartsWith("%PDF-",await File.ReadAllTextAsync(first.OutputPaths[0]));
            if (mode is "capture-visible-png" or "capture-full-png")
                Assert.Equal(new byte[] {137,80,78,71,13,10,26,10},
                    (await File.ReadAllBytesAsync(first.OutputPaths[0]))[..8]);
        }
        finally { Directory.Delete(dir,recursive:true); }
    }
}
