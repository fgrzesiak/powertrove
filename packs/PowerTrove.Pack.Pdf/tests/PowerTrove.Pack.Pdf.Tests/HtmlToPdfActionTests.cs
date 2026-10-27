using FileKit.Html;
using PowerTrove.Pack.Pdf.Html;

namespace PowerTrove.Pack.Pdf.Tests;

public sealed class HtmlToPdfActionTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    private static FileActionServices Services() => FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
    [Fact]
    public async Task RegisteredActionForwardsOptionsAndPublishesNewFilesForBatch()
    {
        var renderer = new Renderer();
        var action = new PdfPack(Services(), htmlRenderer: renderer).Actions.Single(a => a.Descriptor.Id == "powertrove.pdf.html-to-pdf");
        const string html = "<h1>Private document</h1>";
        string first = _directory.Write("first.html", html), second = _directory.Write("second.htm", html);
        _directory.Write("first.pdf", "existing"); var context = Snapshots.ForPaths(first, second);
        Assert.True((await action.MatchAsync(context, default)).IsAvailable);
        var options = Snapshots.Parameters(("Paper", "letter"), ("Landscape", "true"), ("MarginMm", "20"));
        var preview = await action.PreviewAsync(context, options, default); Assert.False(preview.IsBlocked); Assert.DoesNotContain("Private", preview.PreviewText);
        var result = await action.ExecuteAsync(context, options, null, default);
        Assert.True(result.IsSuccess, result.Message); Assert.Equal(2, result.OutputPaths.Count);
        Assert.Equal(new HtmlPrintOptions("letter", true, 20), renderer.Options);
        Assert.Equal("existing", File.ReadAllText(_directory.File("first.pdf"))); Assert.Equal(html, File.ReadAllText(first));
    }
    [Fact]
    public async Task MissingRendererInvalidInputAndFailedPrintDoNotPublishOutput()
    {
        string input = _directory.Write("input.html", "<p>test</p>");
        var unavailable = new HtmlToPdfAction(Services(), null);
        Assert.False((await unavailable.MatchAsync(Snapshots.ForPaths(input), default)).IsAvailable);
        var action = new HtmlToPdfAction(Services(), new Renderer { Fail = true });
        var failure = await action.ExecuteAsync(Snapshots.ForPaths(input), new EmptyParameters(), null, default);
        Assert.False(failure.IsSuccess); Assert.Empty(failure.OutputPaths); Assert.Empty(_directory.NewFiles(input));
        string invalid = _directory.Write("invalid.html", new byte[] { 0xff });
        Assert.True((await action.PreviewAsync(Snapshots.ForPaths(invalid), new EmptyParameters(), default)).IsBlocked);
    }
    private sealed class Renderer : IHtmlPdfRenderer
    {
        public bool IsAvailable => true; public bool Fail { get; init; } public HtmlPrintOptions? Options { get; private set; }
        public Task<HtmlPrintDocument> PrepareAsync(Stream source, CancellationToken cancellationToken) => HtmlPrintDocument.ReadAsync(source, new SafeHtmlMarkupValidator(), cancellationToken);
        public async Task RenderAsync(HtmlPrintDocument document, string destinationPath, HtmlPrintOptions options, CancellationToken cancellationToken)
        {
            Options = options; await File.WriteAllTextAsync(destinationPath, "%PDF-1.4\ncontract-test", cancellationToken);
            if (Fail) throw new HtmlPrintException("print failure");
        }
    }
}
