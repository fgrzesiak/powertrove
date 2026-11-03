using System.Text;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Application.Documents;

namespace PowerTrove.Application.Tests;

public sealed class TextDocumentTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task SavingKeepsLineBreaksEncodingAndByteOrderMark()
    {
        string path = _dir.Write("notes.md", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# Title\r\n\r\nText\r\n")]);
        var (session, failure) = await TextDocumentSession.OpenAsync(path);
        Assert.Equal(TextDocumentLoadFailure.None, failure);
        Assert.Equal("# Title\n\nText\n", session!.SavedText);
        // A WinUI TextBox reports line breaks as lone carriage returns.
        Assert.Equal(TextDocumentSaveResult.Saved, await session.SaveAsync("# Title\r\rText ä\r"));
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# Title\r\n\r\nText ä\r\n")], File.ReadAllBytes(path));
        Assert.Equal("# Title\n\nText ä\n", session.SavedText);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
    }

    [Fact]
    public async Task UnixFilesStayUnixAndUtf16StaysUtf16()
    {
        string unix = _dir.Write("a.md", "a\nb\n");
        var (first, _) = await TextDocumentSession.OpenAsync(unix);
        await first!.SaveAsync("a\rc\r");
        Assert.Equal("a\nc\n", File.ReadAllText(unix));
        string utf16 = _dir.Write("b.md", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("x\r\n")]);
        var (second, _) = await TextDocumentSession.OpenAsync(utf16);
        await second!.SaveAsync("y\r");
        Assert.Equal([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("y\r\n")], File.ReadAllBytes(utf16));
    }

    [Fact]
    public async Task ExternalChangesAreNeverOverwrittenSilently()
    {
        string path = _dir.Write("a.md", "one");
        var (session, _) = await TextDocumentSession.OpenAsync(path);
        File.WriteAllText(path, "changed elsewhere");
        Assert.True(session!.HasChangedOnDisk());
        Assert.Equal(TextDocumentSaveResult.ChangedOnDisk, await session.SaveAsync("mine"));
        Assert.Equal("changed elsewhere", File.ReadAllText(path));
        Assert.Equal(TextDocumentSaveResult.Saved, await session.SaveAsync("mine", overwriteExternalChanges: true));
        Assert.Equal("mine", File.ReadAllText(path));
        Assert.False(session.HasChangedOnDisk());
    }

    [Fact]
    public async Task LegacyCodePageFallsBackToUtf8OnlyWhenNeeded()
    {
        string path = _dir.Write("legacy.md", [(byte)'#', (byte)' ', 0xC4]);
        var (session, _) = await TextDocumentSession.OpenAsync(path);
        Assert.Equal("# Ä", session!.SavedText);
        Assert.Equal(TextDocumentSaveResult.Saved, await session.SaveAsync("# Äb"));
        Assert.Equal(new byte[] { (byte)'#', (byte)' ', 0xC4, (byte)'b' }, File.ReadAllBytes(path));
        Assert.Equal(TextDocumentSaveResult.SavedAsUtf8, await session.SaveAsync("# Ä €→"));
        Assert.Equal("# Ä €→", File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public async Task ReadOnlyFilesAndUnsuitableInputsAreReported()
    {
        string path = _dir.Write("ro.md", "x");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var (session, _) = await TextDocumentSession.OpenAsync(path);
            Assert.Equal(TextDocumentSaveResult.ReadOnly, await session!.SaveAsync("y"));
            Assert.Equal("x", File.ReadAllText(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        Assert.Equal(TextDocumentLoadFailure.TooLarge, (await TextDocumentSession.OpenAsync(_dir.Write("big.md", new byte[TextDocumentSession.MaximumFileBytes + 1]))).Failure);
        Assert.Equal(TextDocumentLoadFailure.NotText, (await TextDocumentSession.OpenAsync(_dir.Write("bin.md", [0x41, 0, 0x42]))).Failure);
        Assert.Equal(TextDocumentLoadFailure.Unreadable, (await TextDocumentSession.OpenAsync(_dir.File("missing.md"))).Failure);
    }

    [Fact]
    public async Task SaveAsContinuesWithTheNewFile()
    {
        string path = _dir.Write("a.md", "a");
        var (session, _) = await TextDocumentSession.OpenAsync(path);
        string copy = _dir.File("copy.md");
        Assert.Equal(TextDocumentSaveResult.Saved, await session!.SaveAsAsync(copy, "b"));
        Assert.Equal("a", File.ReadAllText(path));
        Assert.Equal("b", File.ReadAllText(copy));
        Assert.Equal(copy, session.Path);
    }

    [Fact]
    public async Task RecoveryKeepsDraftsPerDocumentUntilDeletedOrExpired()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new DocumentRecoveryStore(_dir.File("recovery"), clock);
        string a = _dir.File("a.md"), b = _dir.File("b.md");
        await store.SaveAsync(a, "draft A");
        Assert.Equal("draft A", (await store.LoadAsync(a))!.Text);
        Assert.Null(await store.LoadAsync(b));
        store.Delete(a);
        Assert.Null(await store.LoadAsync(a));
        await store.SaveAsync(b, "draft B");
        clock.Now += DocumentRecoveryStore.Retention + TimeSpan.FromMinutes(1);
        Assert.Null(await store.LoadAsync(b));
        Assert.DoesNotContain("draft", new RecoveredDraft("draft", clock.Now).ToString());
    }

    [Fact]
    public async Task RecoveryNeverShowsAnotherDocumentsDraft()
    {
        var store = new DocumentRecoveryStore(_dir.File("recovery"));
        string a = _dir.File("a.md");
        await store.SaveAsync(a, "draft A");
        string file = Assert.Single(Directory.GetFiles(_dir.File("recovery")));
        File.WriteAllText(file, File.ReadAllText(file).Replace("a.md", "other.md", StringComparison.Ordinal));
        Assert.Null(await store.LoadAsync(a));
    }

    [Fact]
    public async Task PreviewServesOnlyVerifiedImagesFromTheDocumentFolder()
    {
        string document = _dir.Write("docs/readme.md", "# x");
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
        _dir.Write("docs/img/red dot.png", png);
        _dir.Write("docs/img/fake.png", "<script></script>");
        _dir.Write("docs/logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");
        _dir.Write("secret.png", png);
        string root = DocumentPreviewData.ResourceBaseUri;
        var image = await DocumentPreviewResources.TryReadAsync(document, root + "img/red%20dot.png?x=1");
        Assert.Equal("image/png", image!.ContentType);
        Assert.Equal("image/svg+xml", (await DocumentPreviewResources.TryReadAsync(document, root + "logo.svg"))!.ContentType);
        Assert.Null(await DocumentPreviewResources.TryReadAsync(document, root + "img/fake.png"));
        Assert.Null(await DocumentPreviewResources.TryReadAsync(document, root + "..%2Fsecret.png"));
        Assert.Null(await DocumentPreviewResources.TryReadAsync(document, root + "readme.md"));
        Assert.Null(await DocumentPreviewResources.TryReadAsync(document, "https://example.com/img/red%20dot.png"));
        Assert.True(DocumentPreviewResources.IsDocument(root + "?r=3"));
        Assert.False(DocumentPreviewResources.IsDocument(root + "img/a.png"));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
