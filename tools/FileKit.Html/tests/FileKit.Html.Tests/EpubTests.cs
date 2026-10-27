using FileKit.Html.Testing;

namespace FileKit.Html.Tests;

public sealed class EpubTests
{
    [Fact]
    public async Task ReadingOrderUsesSpineAndResolvesManifestResourcesWithoutFilesystemExtraction()
    {
        using var stream = new MemoryStream(TestEpubFactory.Create()); var book = await EpubBook.ReadAsync(stream);
        Assert.Equal(new[] { "two", "one" }, book.Chapters.Select(c => c.Id));
        var css = book.ResolveItem(book.Chapters[0].Path, "../styles/book.css"); Assert.Equal("text/css", css.MediaType); Assert.Contains("color:red", book.ReadText(css));
        Assert.Equal("OPS/images/cover art.png", EpubBook.ResolvePath("OPS/chapters/a.xhtml", "../images/cover%20art.png"));
        Assert.Throws<EpubException>(() => book.ResolveItem(book.Chapters[0].Path, "../missing.png"));
    }
    [Theory]
    [InlineData("../../../../outside.png")]
    [InlineData("file:///secret")]
    [InlineData("https://example.invalid/image.png")]
    [InlineData("%2F%2Fhost/image.png")]
    [InlineData("..%5Coutside.png")]
    public void ResourcePathsCannotEscapeTheArchive(string reference) => Assert.Throws<EpubException>(() => EpubBook.ResolvePath("OPS/a.xhtml", reference));
    [Fact]
    public async Task ProtectedUnsafeCorruptAndOversizedInputsFailExplicitly()
    {
        using var encrypted = new MemoryStream(TestEpubFactory.Create(edit: f => f["META-INF/encryption.xml"] = TestEpubFactory.Text("<encryption/>")));
        Assert.Equal(EpubFailure.Protected, (await Assert.ThrowsAsync<EpubException>(() => EpubBook.ReadAsync(encrypted))).Failure);
        using var unsafePath = new MemoryStream(TestEpubFactory.Create(edit: f => f["../outside"] = [1]));
        await Assert.ThrowsAsync<EpubException>(() => EpubBook.ReadAsync(unsafePath));
        using var corrupt = new MemoryStream([1, 2, 3]); await Assert.ThrowsAsync<EpubException>(() => EpubBook.ReadAsync(corrupt));
        using var bomb = new MemoryStream(TestEpubFactory.Create(edit: f => f["large"] = new byte[EpubBook.MaxResourceBytes + 1]));
        Assert.Equal(EpubFailure.Limit, (await Assert.ThrowsAsync<EpubException>(() => EpubBook.ReadAsync(bomb))).Failure);
        using var cancelled = new MemoryStream(TestEpubFactory.Create()); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EpubBook.ReadAsync(cancelled, new(true)));
    }
    [Theory]
    [InlineData("<!DOCTYPE container [<!ENTITY x SYSTEM 'file:///secret'>]><container>&x;</container>")]
    [InlineData("<container/>")]
    public async Task ContainerXmlNeverResolvesEntities(string xml)
    {
        using var stream = new MemoryStream(TestEpubFactory.Create(edit: f => f["META-INF/container.xml"] = TestEpubFactory.Text(xml)));
        await Assert.ThrowsAsync<EpubException>(() => EpubBook.ReadAsync(stream));
    }
    [Fact]
    public async Task MetadataNestingIsBoundedBeforeBuildingTheXmlTree()
    {
        using var stream = new MemoryStream(TestEpubFactory.Create(edit: f => f["META-INF/container.xml"] = TestEpubFactory.Text(string.Concat(Enumerable.Repeat("<a>", 140)) + string.Concat(Enumerable.Repeat("</a>", 140)))));
        Assert.Equal(EpubFailure.Limit, (await Assert.ThrowsAsync<EpubException>(() => EpubBook.ReadAsync(stream))).Failure);
    }
    [Fact]
    public void CssResourcesAndImportsAreRewrittenAndCyclesAreLeftToHostBounds()
    {
        var rewritten = EpubCss.Rewrite("/* comment */p{background:URL('../img.png');content:'url(keep)'} @import 'more.css';", s => "data:image/png;base64,AA==", s => "h1{color:blue}", default);
        Assert.Contains("url(\"data:image/png;base64,AA==\")", rewritten); Assert.Contains("content:'url(keep)'", rewritten); Assert.Contains("h1{color:blue}", rewritten);
        Assert.Throws<EpubException>(() => EpubCss.Rewrite("@import 'book.css' screen;", s => s, s => s, default));
        Assert.Throws<EpubException>(() => EpubCss.Rewrite("p{background:u\\72l(remote)}", s => s, s => s, default));
        Assert.Throws<EpubException>(() => EpubCss.Rewrite("</style><script>active()</script>", s => s, s => s, default));
    }
}
