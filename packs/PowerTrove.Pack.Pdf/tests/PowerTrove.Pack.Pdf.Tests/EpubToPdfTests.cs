using FileKit.Html;
using FileKit.Html.Testing;
using PowerTrove.Pack.Pdf.Html;

namespace PowerTrove.Pack.Pdf.Tests;

public sealed class EpubToPdfTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    [Fact]
    public async Task BuilderEmbedsInternalStylesAndImagesInSpineOrder()
    {
        using var input = new MemoryStream(TestEpubFactory.Create(first: "<h1>Chapter one</h1><img src='../images/blue.png'>", image: [1, 2, 3]));
        var book = await EpubBook.ReadAsync(input); var document = new EpubDocumentBuilder().Build(book, default);
        Assert.Contains("data:image/png;base64,AQID", document.Html); Assert.Contains("color:red", document.Html);
        Assert.True(document.Html.IndexOf("Second chapter", StringComparison.Ordinal) < document.Html.IndexOf("Chapter one", StringComparison.Ordinal));
        Assert.DoesNotContain("href=", document.Html); Assert.DoesNotContain("../images", document.Html); Assert.DoesNotContain("<link", document.Html);
    }
    [Fact]
    public async Task EpubTwoUtf8MetadataAndStandardXhtmlDoctypeDoNotLoadExternalResources()
    {
        using var input = new MemoryStream(TestEpubFactory.Create(edit: f =>
        {
            f["OPS/package.opf"] = TestEpubFactory.Text(System.Text.Encoding.UTF8.GetString(f["OPS/package.opf"]).Replace("version='3.0'", "version='2.0'", StringComparison.Ordinal));
            f["OPS/chapters/one.xhtml"] = TestEpubFactory.Text("<!DOCTYPE html PUBLIC '-//W3C//DTD XHTML 1.1//EN' 'http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd'><html xmlns='http://www.w3.org/1999/xhtml' lang='de'><head><meta http-equiv='Content-Type' content='text/html; charset=utf-8'/></head><body><p>Grüße</p></body></html>");
        }));
        var result = new EpubDocumentBuilder().Build(await EpubBook.ReadAsync(input), default);
        Assert.Contains("Grüße", result.Html); Assert.Contains("lang=\"de\"", result.Html); Assert.DoesNotContain("xhtml11.dtd", result.Html);
    }
    [Theory]
    [InlineData("<script>active()</script>", "h1{color:red}")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>", "h1{color:red}")]
    [InlineData("<img src='https://example.invalid/track.png'>", "h1{color:red}")]
    [InlineData("<h1>test</h1>", "@import 'book.css';")]
    [InlineData("<h1>test</h1>", "p{background:url('file:///private')}")]
    public async Task BuilderRefusesActiveExternalAndRecursiveContent(string chapter, string css)
    {
        using var input = new MemoryStream(TestEpubFactory.Create(first: chapter, css: css)); var book = await EpubBook.ReadAsync(input);
        var exception = Record.Exception(() => new EpubDocumentBuilder().Build(book, default)); Assert.True(exception is EpubException or HtmlPrintException);
    }
    [Fact]
    public async Task RegisteredBatchActionUsesSharedAtomicOutputAndConfiguredPaper()
    {
        var services = FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()); var renderer = new Renderer();
        var action = new PdfPack(services, htmlRenderer: renderer, epubBuilder: new EpubDocumentBuilder()).Actions.Single(a => a.Descriptor.Id == "powertrove.pdf.epub-to-pdf");
        byte[] epub = TestEpubFactory.Create(); string one = _directory.Write("one.epub", epub), two = _directory.Write("two.epub", epub); _directory.Write("one.pdf", "existing");
        var context = Snapshots.ForPaths(one, two); var parameters = Snapshots.Parameters(("Paper", "letter"), ("MarginMm", "15"));
        Assert.True((await action.MatchAsync(context, default)).IsAvailable); Assert.False((await action.PreviewAsync(context, parameters, default)).IsBlocked);
        var result = await action.ExecuteAsync(context, parameters, null, default);
        Assert.True(result.IsSuccess, result.Message); Assert.Equal(2, result.OutputPaths.Count); Assert.Equal(new HtmlPrintOptions("letter", false, 15), renderer.Options);
        Assert.Equal(epub, File.ReadAllBytes(one)); Assert.Equal("existing", File.ReadAllText(_directory.File("one.pdf")));
        string protectedBook = _directory.Write("protected.epub", TestEpubFactory.Create(edit: f => f["META-INF/encryption.xml"] = [1]));
        Assert.True((await action.PreviewAsync(Snapshots.ForPaths(protectedBook), new EmptyParameters(), default)).IsBlocked);
        Assert.False((await action.ExecuteAsync(Snapshots.ForPaths(protectedBook), new EmptyParameters(), null, default)).IsSuccess);
        Assert.False(File.Exists(_directory.File("protected.pdf")));
        var absent = new EpubToPdfAction(services, null, new EpubDocumentBuilder()); Assert.False((await absent.MatchAsync(context, default)).IsAvailable);
    }
    private sealed class Renderer : IHtmlPdfRenderer
    {
        public bool IsAvailable => true; public HtmlPrintOptions? Options { get; private set; }
        public Task<HtmlPrintDocument> PrepareAsync(Stream source, CancellationToken ct) => throw new NotSupportedException();
        public async Task RenderAsync(HtmlPrintDocument document, string path, HtmlPrintOptions options, CancellationToken ct)
        { Options = options; await File.WriteAllTextAsync(path, "%PDF-1.4\ncontract-test", ct); }
    }
}
