using System.Text;
using FileKit.Html;
using PowerTrove.Pack.Markdown.Markdig;

namespace PowerTrove.Pack.Markdown.Tests;

public sealed class MarkdownActionTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FileActionServices _services = FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
    private readonly MarkdigMarkdownEngine _engine = new();
    public void Dispose() => _dir.Dispose();

    internal static byte[] Png()
    {
        // 1x1 red PNG.
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
    }

    [Fact]
    public async Task ViewPreviewRendersTheFileWithLocalImagesFromTheDocumentOrigin()
    {
        _dir.Write("img/red dot.png", Png());
        _dir.Write("secret.png", Png());
        string path = _dir.Write("docs/readme.md", "# Readme\n\n![dot](../img/red%20dot.png) ![remote](https://example.com/x.png) ![escape](../../secret.png)\n");
        // The document's folder is docs/, so ../img is outside it and refused like the escape.
        string inside = _dir.Write("guide.md", "# Guide\n\n![dot](img/red%20dot.png) ![remote](https://example.com/x.png) ![abs](C:/Windows/win.ini)\n");
        var action = new MarkdownViewAction(_services, _engine, edit: false);

        var preview = await action.PreviewAsync(Snapshots.ForPaths(inside), DocumentPreviewRequest.Create(), default);

        Assert.False(preview.IsBlocked);
        var document = Assert.IsType<DocumentPreviewData>(preview.DocumentPreview);
        Assert.True(document.IsValid);
        Assert.Equal("Guide", document.Title);
        Assert.Contains("src=\"https://document.powertrove.example/img/red%20dot.png\"", document.Html);
        Assert.DoesNotContain("example.com/x.png", document.Html);
        Assert.DoesNotContain("win.ini", document.Html);
        Assert.Contains("Content-Security-Policy", document.Html);
        Assert.Contains(preview.Warnings, w => w.Contains("1 images from the internet"));
        Assert.Contains(preview.Warnings, w => w.Contains("1 images are outside"));

        var outside = await action.PreviewAsync(Snapshots.ForPaths(path), DocumentPreviewRequest.Create(), default);
        Assert.DoesNotContain("red%20dot", outside.DocumentPreview!.Html);
    }

    [Fact]
    public async Task ViewPreviewWithoutRequestReturnsNoDocument()
    {
        string path = _dir.Write("a.md", "# A");
        var preview = await new MarkdownViewAction(_services, _engine, edit: false).PreviewAsync(Snapshots.ForPaths(path), new EmptyParameters(), default);
        Assert.False(preview.IsBlocked);
        Assert.Null(preview.DocumentPreview);
    }

    [Fact]
    public async Task EditPreviewRendersTheUnsavedDraftAndLeavesTheFileUnchanged()
    {
        string path = _dir.Write("notes.md", "# Saved");
        var action = new MarkdownViewAction(_services, _engine, edit: true);

        var preview = await action.PreviewAsync(Snapshots.ForPaths(path), DocumentPreviewRequest.Create(draft: "# Draft\n\n**bold**"), default);

        Assert.Contains("<strong>bold</strong>", preview.DocumentPreview!.Html);
        Assert.Equal("Draft", preview.DocumentPreview.Title);
        Assert.Equal("# Saved", File.ReadAllText(path));
        Assert.Equal([path], _dir.NewFiles().Where(f => f.EndsWith(".md")).ToArray());
    }

    [Fact]
    public async Task ViewerActionsDoNotRunOutsideTheViewer()
    {
        string path = _dir.Write("a.md", "# A");
        var receipt = await new MarkdownViewAction(_services, _engine, edit: true).ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, default);
        Assert.False(receipt.IsSuccess);
        Assert.Contains(receipt.Failures, f => f.ErrorMessage.Contains("palette"));
        Assert.Equal([path], _dir.NewFiles());
    }

    [Fact]
    public async Task ViewerMatchesOneMarkdownFileOnly()
    {
        string a = _dir.Write("a.md", "a"), b = _dir.Write("b.markdown", "b"), t = _dir.Write("c.txt", "c");
        var action = new MarkdownViewAction(_services, _engine, edit: false);
        Assert.True((await action.MatchAsync(Snapshots.ForPaths(b), default)).IsAvailable);
        Assert.False((await action.MatchAsync(Snapshots.ForPaths(a, b), default)).IsAvailable);
        Assert.False((await action.MatchAsync(Snapshots.ForPaths(t), default)).IsAvailable);
    }

    [Fact]
    public async Task OversizedDocumentsAreBlockedInThePreview()
    {
        string path = _dir.Write("big.md", new string('x', (int)MarkdownViewAction.MaximumViewBytes + 1));
        var preview = await new MarkdownViewAction(_services, _engine, edit: false).PreviewAsync(Snapshots.ForPaths(path), DocumentPreviewRequest.Create(), default);
        Assert.True(preview.IsBlocked);
        Assert.Null(preview.DocumentPreview);
    }

    [Fact]
    public async Task HtmlExportEmbedsLocalImagesAndKeepsTheSource()
    {
        _dir.Write("img/dot.png", Png());
        _dir.Write("img/fake.png", Encoding.UTF8.GetBytes("<script>not an image</script>"));
        string path = _dir.Write("readme.md", "# Readme\n\n![dot](img/dot.png) ![fake](img/fake.png) ![gone](img/missing.png) ![web](https://example.com/w.png)\n<script>alert(1)</script>");
        string before = File.ReadAllText(path);
        var action = new MarkdownToHtmlAction(_services, _engine);

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), Snapshots.Parameters(("Images", "embed")), null, default);

        Assert.True(receipt.IsSuccess);
        string html = File.ReadAllText(Assert.Single(receipt.OutputPaths));
        Assert.EndsWith("readme.html", receipt.OutputPaths[0]);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("src=\"data:image/png;base64,", html);
        Assert.Contains("<span class=\"image-missing\">fake</span>", html);
        Assert.Contains("<span class=\"image-missing\">gone</span>", html);
        Assert.Contains("src=\"https://example.com/w.png\"", html);
        Assert.DoesNotContain("alert", html);
        Assert.Contains("script-src", html.Replace("default-src 'none'", "script-src"));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public async Task HtmlExportCanLinkImagesInstead()
    {
        _dir.Write("img/dot.png", Png());
        string path = _dir.Write("readme.md", "![dot](img/dot.png)");
        var receipt = await new MarkdownToHtmlAction(_services, _engine).ExecuteAsync(Snapshots.ForPaths(path), Snapshots.Parameters(("Images", "link")), null, default);
        string html = File.ReadAllText(Assert.Single(receipt.OutputPaths));
        Assert.Contains("src=\"img/dot.png\"", html);
        Assert.DoesNotContain("base64", html);
    }

    [Fact]
    public async Task ExistingOutputsAreNeverOverwritten()
    {
        string path = _dir.Write("readme.md", "# New");
        string existing = _dir.Write("readme.html", "keep");
        var receipt = await new MarkdownToHtmlAction(_services, _engine).ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, default);
        Assert.True(receipt.IsSuccess);
        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.NotEqual(existing, Assert.Single(receipt.OutputPaths));
    }

    [Fact]
    public async Task PdfExportPrintsTheSanitizedDocumentWithEmbeddedImages()
    {
        _dir.Write("dot.png", Png());
        string path = _dir.Write("report.md", "# Report\n\n![dot](dot.png)\n\n<img src=x onerror=alert(1)>");
        var printer = new CapturingPrinter();
        var action = new MarkdownToPdfAction(_services, _engine, printer);

        var preview = await action.PreviewAsync(Snapshots.ForPaths(path), new EmptyParameters(), default);
        Assert.False(preview.IsBlocked);
        Assert.EndsWith("report.pdf", Assert.Single(preview.ExpectedOutputs));
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), Snapshots.Parameters(("Paper", "letter"), ("Landscape", "true"), ("MarginMm", "5")), null, default);

        Assert.True(receipt.IsSuccess);
        Assert.Equal(new HtmlPrintOptions("letter", true, 5), printer.Options);
        Assert.Contains("data:image/png;base64,", printer.Html);
        Assert.Contains("<h1 id=\"report\">Report</h1>", printer.Html);
        Assert.DoesNotContain("onerror", printer.Html);
        Assert.DoesNotContain("src=\"x\"", printer.Html);
        Assert.True(File.ReadAllBytes(receipt.OutputPaths[0]).AsSpan().StartsWith("%PDF-"u8));
    }

    [Fact]
    public async Task PdfExportIsUnavailableWithoutEdge()
    {
        string path = _dir.Write("a.md", "# A");
        var availability = await new MarkdownToPdfAction(_services, _engine, null).MatchAsync(Snapshots.ForPaths(path), default);
        Assert.False(availability.IsAvailable);
        Assert.Contains("Edge", availability.Reason);
    }

    [Fact]
    public async Task HtmlToMarkdownCreatesAMarkdownFile()
    {
        string path = _dir.Write("page.htm", "<html><body><h1>Hi</h1><p>Text <script>x()</script></p></body></html>");
        var receipt = await new ToMarkdownAction(_services, _engine, null).ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, default);
        Assert.True(receipt.IsSuccess);
        Assert.Equal("# Hi\n\nText\n", File.ReadAllText(Assert.Single(receipt.OutputPaths)));
    }

    [Fact]
    public async Task HtmlWithoutTextFailsClearly()
    {
        string path = _dir.Write("empty.html", "<html><body><script>x()</script></body></html>");
        var receipt = await new ToMarkdownAction(_services, _engine, null).ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, default);
        Assert.False(receipt.IsSuccess);
        Assert.Contains(receipt.Failures, f => f.ErrorMessage.Contains("no text"));
        Assert.Equal([path], _dir.NewFiles());
    }

    [Fact]
    public async Task Windows1252MarkdownIsDecoded()
    {
        string path = _dir.Write("legacy.md", [(byte)'#', (byte)' ', 0xC4, (byte)'r', (byte)'g', (byte)'e', (byte)'r']);
        var preview = await new MarkdownViewAction(_services, _engine, edit: false).PreviewAsync(Snapshots.ForPaths(path), DocumentPreviewRequest.Create(), default);
        Assert.Equal("Ärger", preview.DocumentPreview!.Title);
    }

    [Fact]
    public void GermanResourcesCoverEveryEnglishString()
    {
        var english = Resources(""); var german = Resources(".de");
        Assert.Equal(english.Keys.Order(), german.Keys.Order());
        Assert.All(german, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));
    }

    private static Dictionary<string, string> Resources(string culture)
    {
        string file = Path.Combine(FindRoot(), "packs", "PowerTrove.Pack.Markdown", "src", "PowerTrove.Pack.Markdown", "Resources", "Strings" + culture + ".resx");
        return System.Xml.Linq.XDocument.Load(file).Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PowerTrove.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }

    private sealed class CapturingPrinter : ILocalHtmlPdfPrinter
    {
        public string Html { get; private set; } = string.Empty;
        public HtmlPrintOptions? Options { get; private set; }
        public bool IsAvailable => true;
        public Task<HtmlPrintDocument> PrepareAsync(Stream source, CancellationToken cancellationToken) =>
            HtmlPrintDocument.ReadAsync(source, new MarkdownPrintValidatorAccess(), cancellationToken);
        public async Task RenderAsync(HtmlPrintDocument document, string destinationPath, HtmlPrintOptions options, CancellationToken cancellationToken)
        {
            Html = document.Html; Options = options;
            await File.WriteAllBytesAsync(destinationPath, "%PDF-1.7\n"u8.ToArray(), cancellationToken);
        }
    }

    private sealed class MarkdownPrintValidatorAccess : IHtmlMarkupValidator
    {
        private readonly IHtmlMarkupValidator _inner = new MarkdownPrintValidator();
        public string ValidateAndNormalize(string html) => _inner.ValidateAndNormalize(html);
    }
}
