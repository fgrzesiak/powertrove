using System.Text.Json;
using FileKit.Html;
using PowerTrove.Pack.Markdown.Markdig;

namespace PowerTrove.Pack.Markdown.Tests;

public sealed class MergeMarkdownTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FileActionServices _services = FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
    private readonly MarkdigMarkdownEngine _engine = new();
    public void Dispose() => _dir.Dispose();

    private MergeMarkdownAction Action(ILocalHtmlPdfPrinter? printer = null) => new(_services, _engine, printer);

    private async Task<IReadOnlyList<ActionParameterChoice>> ChoicesAsync(params string[] paths)
    {
        var preview = await Action().PreviewAsync(Snapshots.ForPaths(paths), new EmptyParameters(), default);
        return Assert.Single(preview.ParameterChoices!).Choices;
    }

    private static string Order(IReadOnlyList<ActionParameterChoice> choices, params int[] indexes) =>
        JsonSerializer.Serialize(indexes.Select(i => choices[i].Value));

    [Fact]
    public void MergePartsRelabelFootnotesRebaseLinksAndDropLaterFrontMatter()
    {
        string markdown = "---\ntitle: second\n---\n# Two\n\nText[^1] with ![dot](img/dot.png) and [ref].\n\n[^1]: A note.\n\n[ref]: <docs/a b.md>\n\n```\nunclosed";
        string first = _engine.PrepareMergePart(markdown, new(_ => null, null, KeepFrontMatter: true), default);
        Assert.Equal(markdown + "\n```\n", first);

        string part = _engine.PrepareMergePart(markdown, new(url => "../b/" + url.Replace(" ", "%20"), "-2", KeepFrontMatter: false), default);
        Assert.StartsWith("# Two", part);
        Assert.Contains("Text[^1-2] with ![dot](../b/img/dot.png)", part);
        Assert.Contains("[^1-2]: A note.", part);
        Assert.Contains("[ref]: <../b/docs/a%20b.md>", part);
        Assert.EndsWith("unclosed\n```\n", part);
    }

    [Fact]
    public void LinksAreRebasedOnlyWhenTheyAreRelativeAndTheFolderChanges()
    {
        string a = _dir.File("a"), b = _dir.File("b");
        Assert.Equal("../b/img/my%20dot%281%29.png?x#y", MergeMarkdownAction.Rebase("img/my%20dot(1).png?x#y", b, a));
        Assert.Equal("../b/notes/", MergeMarkdownAction.Rebase("notes/", b, a));
        Assert.Null(MergeMarkdownAction.Rebase("img/dot.png", a, a));
        Assert.Null(MergeMarkdownAction.Rebase("https://example.com/x.png", b, a));
        Assert.Null(MergeMarkdownAction.Rebase("#intro", b, a));
        Assert.Null(MergeMarkdownAction.Rebase("C:/x.png", b, a));
        Assert.Null(MergeMarkdownAction.Rebase("/root.png", b, a));
    }

    [Fact]
    public async Task MarkdownFilesAreJoinedInTheChosenOrderNextToTheFirstChosenFile()
    {
        string intro = _dir.Write("book/01 intro.md", "---\ntitle: Book\n---\n# Intro\n\nSee[^1].\n\n[^1]: Intro note.\n");
        string chapter = _dir.Write("chapters/02 chapter.md", "# Chapter\n\n![dot](img/dot.png)\n\nMore[^1].\n\n[^1]: Chapter note.\n");
        string end = _dir.Write("book/03 end.md", "# End\n");
        var choices = await ChoicesAsync(intro, chapter, end);
        Assert.Equal(["01 intro.md", "02 chapter.md", "03 end.md"], choices.Select(c => c.Title));
        Assert.DoesNotContain(choices, c => c.Value.Contains("intro", StringComparison.OrdinalIgnoreCase));

        var parameters = Snapshots.Parameters(("Order", Order(choices, 2, 1, 0)), ("Divider", "true"));
        var preview = await Action().PreviewAsync(Snapshots.ForPaths(intro, chapter, end), parameters, default);
        Assert.False(preview.IsBlocked);
        Assert.Equal(Path.Combine(_dir.File("book"), "03 end (merged).md"), Assert.Single(preview.ExpectedOutputs));

        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(intro, chapter, end), parameters, null, default);
        Assert.True(receipt.IsSuccess);
        string merged = File.ReadAllText(Assert.Single(receipt.OutputPaths));
        Assert.Equal(Path.Combine(_dir.File("book"), "03 end (merged).md"), receipt.OutputPaths[0]);
        Assert.Equal("# End\n\n---\n\n# Chapter\n\n![dot](../chapters/img/dot.png)\n\nMore[^1-2].\n\n[^1-2]: Chapter note.\n\n---\n\n# Intro\n\nSee[^1-3].\n\n[^1-3]: Intro note.\n", merged);

        string html = _engine.Render(merged, default).ToHtml(_ => null, default);
        Assert.Contains("Chapter note.", html);
        Assert.Contains("Intro note.", html);
    }

    [Fact]
    public async Task ClearedFilesAreLeftOutAndAtLeastTwoMustRemain()
    {
        string a = _dir.Write("a.md", "# A\n"), b = _dir.Write("b.md", "# B\n"), c = _dir.Write("c.md", "# C\n");
        var choices = await ChoicesAsync(a, b, c);

        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(a, b, c), Snapshots.Parameters(("Order", Order(choices, 2, 0))), null, default);
        Assert.True(receipt.IsSuccess);
        Assert.Equal("# C\n\n# A\n", File.ReadAllText(receipt.OutputPaths[0]));

        var one = await Action().PreviewAsync(Snapshots.ForPaths(a, b, c), Snapshots.Parameters(("Order", Order(choices, 1))), default);
        Assert.True(one.IsBlocked);
        Assert.NotNull(one.ParameterChoices);

        var invalid = await Action().PreviewAsync(Snapshots.ForPaths(a, b, c), Snapshots.Parameters(("Order", "not json")), default);
        Assert.True(invalid.IsBlocked);
    }

    [Fact]
    public async Task AnOrderForAnotherSelectionKeepsTheDefaultOrder()
    {
        string a = _dir.Write("a.md", "# A\n"), b = _dir.Write("b.md", "# B\n");
        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(b, a), Snapshots.Parameters(("Order", "[\"0123456789ab\"]")), null, default);
        Assert.True(receipt.IsSuccess);
        Assert.Equal("# A\n\n# B\n", File.ReadAllText(receipt.OutputPaths[0]));
    }

    [Fact]
    public async Task HtmlMergeMakesOnePageWithSeparateAnchorsAndEachFilesImages()
    {
        _dir.Write("one/dot.png", MarkdownActionTests.Png());
        _dir.Write("two/dot.png", MarkdownActionTests.Png());
        string one = _dir.Write("one/a.md", "# Same\n\n![dot](dot.png) [jump](#same)[^1]\n\n[^1]: note");
        string two = _dir.Write("two/b.md", "# Same\n\n![dot](dot.png) [jump](#same)[^1]\n\n[^1]: note");

        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(one, two), Snapshots.Parameters(("Format", "html")), null, default);

        Assert.True(receipt.IsSuccess);
        Assert.Equal(Path.Combine(_dir.File("one"), "a (merged).html"), Assert.Single(receipt.OutputPaths));
        string html = File.ReadAllText(receipt.OutputPaths[0]);
        Assert.Equal(2, html.Split("data:image/png;base64,").Length - 1);
        Assert.Contains("<h1 id=\"same\">", html);
        Assert.Contains("<h1 id=\"part-2-same\">", html);
        Assert.Contains("href=\"#part-2-same\"", html);
        Assert.Contains("id=\"part-2-fn:1\"", html);
        Assert.Equal(2, html.Split("<section class=\"merged-part\">").Length - 1);
        Assert.Equal(1, html.Split("<title>").Length - 1);
    }

    [Fact]
    public async Task PdfMergePrintsOneDocumentWithPageBreaks()
    {
        string a = _dir.Write("a.md", "# A"), b = _dir.Write("b.md", "# B");
        var printer = new CapturingPrinter();

        var receipt = await Action(printer).ExecuteAsync(Snapshots.ForPaths(a, b), Snapshots.Parameters(("Format", "pdf"), ("Landscape", "true")), null, default);

        Assert.True(receipt.IsSuccess);
        Assert.EndsWith("a (merged).pdf", Assert.Single(receipt.OutputPaths));
        Assert.Contains("break-before: page", printer.Html);
        Assert.True(printer.Html.IndexOf("id=\"a\"", StringComparison.Ordinal) < printer.Html.IndexOf("id=\"part-2-b\"", StringComparison.Ordinal));
        Assert.Equal(new HtmlPrintOptions("a4", true, 15), printer.Options);

        var noPages = new CapturingPrinter();
        await Action(noPages).ExecuteAsync(Snapshots.ForPaths(a, b), Snapshots.Parameters(("Format", "pdf"), ("PageBreaks", "false")), null, default);
        Assert.DoesNotContain("break-before", noPages.Html);
    }

    [Fact]
    public async Task PdfMergeNeedsEdgeButMarkdownMergeDoesNot()
    {
        string a = _dir.Write("a.md", "# A"), b = _dir.Write("b.md", "# B");
        Assert.True((await Action().MatchAsync(Snapshots.ForPaths(a, b), default)).IsAvailable);
        Assert.False((await Action().MatchAsync(Snapshots.ForPaths(a), default)).IsAvailable);
        var pdf = await Action().PreviewAsync(Snapshots.ForPaths(a, b), Snapshots.Parameters(("Format", "pdf")), default);
        Assert.True(pdf.IsBlocked);
        Assert.Contains("Edge", pdf.PreviewText);
    }

    [Fact]
    public async Task ConvertingSeveralFilesPointsToTheCombineOption()
    {
        string a = _dir.Write("a.md", "# A"), b = _dir.Write("b.md", "# B");
        var action = new MarkdownToHtmlAction(_services, _engine);
        var several = await action.PreviewAsync(Snapshots.ForPaths(a, b), new EmptyParameters(), default);
        var single = await action.PreviewAsync(Snapshots.ForPaths(a), new EmptyParameters(), default);
        var combined = await action.PreviewAsync(Snapshots.ForPaths(a, b), Snapshots.Parameters(("Combine", "true")), default);
        Assert.Contains(several.Warnings, w => w.Contains("Combine all files into one"));
        Assert.Equal(2, several.ExpectedOutputs.Count);
        Assert.DoesNotContain(single.Warnings, w => w.Contains("Combine all files into one"));
        Assert.Null(single.ParameterChoices);
        Assert.DoesNotContain(combined.Warnings, w => w.Contains("Combine all files into one"));
        Assert.EndsWith("a (merged).html", Assert.Single(combined.ExpectedOutputs));
        Assert.Equal(2, Assert.Single(combined.ParameterChoices!).Choices.Count);
    }

    [Fact]
    public async Task HtmlConversionCanCombineAllFilesInTheChosenOrder()
    {
        string a = _dir.Write("a.md", "# A"), b = _dir.Write("b.md", "# B"), c = _dir.Write("c.md", "# C");
        var choices = await ChoicesAsync(a, b, c);
        var parameters = Snapshots.Parameters(("Combine", "true"), ("Order", Order(choices, 1, 0)), ("Images", "link"));

        var receipt = await new MarkdownToHtmlAction(_services, _engine).ExecuteAsync(Snapshots.ForPaths(a, b, c), parameters, null, default);

        Assert.True(receipt.IsSuccess);
        Assert.EndsWith("b (merged).html", Assert.Single(receipt.OutputPaths));
        string html = File.ReadAllText(receipt.OutputPaths[0]);
        Assert.True(html.IndexOf("id=\"b\"", StringComparison.Ordinal) < html.IndexOf("id=\"part-2-a\"", StringComparison.Ordinal));
        Assert.DoesNotContain(">C</h1>", html);
        Assert.False(File.Exists(_dir.File("a.html")));
    }

    [Fact]
    public async Task PdfConversionCanCombineAllFilesIntoOnePdf()
    {
        string a = _dir.Write("a.md", "# A"), b = _dir.Write("b.md", "# B");
        var printer = new CapturingPrinter();
        var parameters = Snapshots.Parameters(("Combine", "true"), ("Paper", "letter"), ("PageBreaks", "false"), ("Divider", "true"));

        var receipt = await new MarkdownToPdfAction(_services, _engine, printer).ExecuteAsync(Snapshots.ForPaths(a, b), parameters, null, default);

        Assert.True(receipt.IsSuccess);
        Assert.EndsWith("a (merged).pdf", Assert.Single(receipt.OutputPaths));
        Assert.Equal(new HtmlPrintOptions("letter", false, 15), printer.Options);
        Assert.Contains("<hr>", printer.Html);
        Assert.DoesNotContain("break-before", printer.Html);
        Assert.Contains("id=\"part-2-b\"", printer.Html);

        var single = await new MarkdownToPdfAction(_services, _engine, printer).ExecuteAsync(Snapshots.ForPaths(a), Snapshots.Parameters(("Combine", "true")), null, default);
        Assert.True(single.IsSuccess);
        Assert.EndsWith("a.pdf", Assert.Single(single.OutputPaths));
    }

    private sealed class CapturingPrinter : ILocalHtmlPdfPrinter
    {
        public string Html { get; private set; } = string.Empty;
        public HtmlPrintOptions? Options { get; private set; }
        public bool IsAvailable => true;
        public Task<HtmlPrintDocument> PrepareAsync(Stream source, CancellationToken cancellationToken) =>
            HtmlPrintDocument.ReadAsync(source, new MarkdownPrintValidator(), cancellationToken);
        public async Task RenderAsync(HtmlPrintDocument document, string destinationPath, HtmlPrintOptions options, CancellationToken cancellationToken)
        {
            Html = document.Html; Options = options;
            await File.WriteAllBytesAsync(destinationPath, "%PDF-1.7\n"u8.ToArray(), cancellationToken);
        }
    }
}
