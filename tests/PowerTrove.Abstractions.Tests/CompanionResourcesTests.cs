namespace PowerTrove.Abstractions.Tests;

public sealed class CompanionResourcesTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("img/a.png", true)]
    [InlineData("img\\a.png", true)]
    [InlineData("./a.png", true)]
    [InlineData("img/../a.png", true)]
    [InlineData("../a.png", false)]
    [InlineData("img/../../a.png", false)]
    [InlineData("/a.png", false)]
    [InlineData(@"\\server\share\a.png", false)]
    [InlineData("C:/Windows/win.ini", false)]
    [InlineData("a.png:stream", false)]
    [InlineData("a.png.", false)]
    [InlineData("folder /a.png", false)]
    [InlineData("", false)]
    public void OnlyRelativeReferencesInsideTheDocumentFolderResolve(string reference, bool expected)
    {
        string document = _dir.File("docs/readme.md");
        Assert.Equal(expected, CompanionResources.TryResolve(document, reference, out string path));
        if (expected) Assert.StartsWith(Path.GetFullPath(_dir.File("docs")) + Path.DirectorySeparatorChar, path);
    }

    [Fact]
    public void PhysicalFileSystemReadsCompanionsAndRefusesEscapes()
    {
        string document = _dir.Write("docs/readme.md", "# x");
        _dir.Write("docs/img/a.png", [1, 2, 3]);
        _dir.Write("secret.txt", "secret");
        var fs = new PhysicalOutputFileSystem();
        using (var stream = fs.OpenCompanionRead(document, "img/a.png")) Assert.Equal(3, stream.Length);
        Assert.Throws<UnauthorizedAccessException>(() => fs.OpenCompanionRead(document, "../secret.txt"));
        Assert.Throws<FileNotFoundException>(() => fs.OpenCompanionRead(document, "img/missing.png"));
    }

    [Fact]
    public void OtherFileSystemsGrantNoCompanionReadsByDefault()
    {
        IOutputFileSystem fs = new NoCompanions();
        Assert.Throws<UnauthorizedAccessException>(() => fs.OpenCompanionRead(_dir.File("a.md"), "a.png"));
    }

    [Fact]
    public void DocumentPreviewRequestCarriesTheDraftOnlyWhenRequested()
    {
        var saved = new Dictionary<string, string> { ["Option"] = "1", [DocumentPreviewRequest.DraftParameterName] = "stale" };
        var request = DocumentPreviewRequest.Create(saved, "# Draft");
        Assert.True(DocumentPreviewRequest.IsRequested(request));
        Assert.Equal("# Draft", DocumentPreviewRequest.Draft(request));
        Assert.Null(DocumentPreviewRequest.Draft(DocumentPreviewRequest.Create(saved)));
        Assert.Null(DocumentPreviewRequest.Draft(new DictionaryParameters(saved)));
        Assert.Equal("1", request.Values["Option"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentPreviewRequest.Create(null, new string('x', DocumentPreviewRequest.MaximumDraftLength + 1)));
        Assert.DoesNotContain("secret", new DocumentPreviewData("<p>secret</p>").ToString());
    }

    private sealed class NoCompanions : IOutputFileSystem
    {
        public bool FileExists(string path) => false;
        public bool DirectoryExists(string path) => false;
        public void CreateDirectory(string path) { }
        public void DeleteFile(string path) { }
        public Stream OpenRead(string path) => Stream.Null;
        public Stream CreateFile(string path) => Stream.Null;
        public long GetFileLength(string path) => 0;
        public void MoveFile(string sourcePath, string destinationPath) { }
        public void MoveDirectory(string sourcePath, string destinationPath) { }
        public void DeleteDirectory(string path) { }
    }
}
