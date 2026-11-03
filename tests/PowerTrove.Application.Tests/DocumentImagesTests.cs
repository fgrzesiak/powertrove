using PowerTrove.Abstractions.Testing;
using PowerTrove.Application.Documents;

namespace PowerTrove.Application.Tests;

public sealed class DocumentImagesTests : IDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ImagesFromElsewhereAreCopiedIntoTheImagesFolderOnce()
    {
        string document = _dir.Write("docs/readme.md", "# x");
        string picture = _dir.Write("Downloads/My Photo (1).png", Png);

        var first = await DocumentImages.ImportAsync(document, picture);
        Assert.Equal(ImageImportFailure.None, first.Failure);
        Assert.True(first.Copied);
        Assert.Equal("images/My-Photo-_1.png", first.RelativePath);
        Assert.Equal(Png, File.ReadAllBytes(_dir.File("docs/images/My-Photo-_1.png")));
        Assert.True(File.Exists(picture));

        var again = await DocumentImages.ImportAsync(document, picture);
        Assert.Equal(first.RelativePath, again.RelativePath);
        Assert.False(again.Copied);
        Assert.Single(Directory.GetFiles(_dir.File("docs/images")));
    }

    [Fact]
    public async Task ADifferentImageWithTheSameNameNeverOverwritesTheCopy()
    {
        string document = _dir.Write("docs/readme.md", "# x");
        _dir.Write("docs/images/photo.png", [.. Png, 0]);
        var import = await DocumentImages.ImportAsync(document, _dir.Write("elsewhere/photo.png", Png));
        Assert.Equal("images/photo (2).png", import.RelativePath);
        Assert.Equal([.. Png, 0], File.ReadAllBytes(_dir.File("docs/images/photo.png")));
    }

    [Fact]
    public async Task ImagesInsideTheDocumentFolderAreReferencedWhereTheyAre()
    {
        string document = _dir.Write("docs/readme.md", "# x");
        var import = await DocumentImages.ImportAsync(document, _dir.Write("docs/shots/a b.png", Png));
        Assert.Equal("shots/a b.png", import.RelativePath);
        Assert.False(import.Copied);
        Assert.False(Directory.Exists(_dir.File("docs/images")));
    }

    [Fact]
    public async Task OnlyRealImagesAreImported()
    {
        string document = _dir.Write("docs/readme.md", "# x");
        var fake = await DocumentImages.ImportAsync(document, _dir.Write("fake.png", "not an image"));
        Assert.Equal(ImageImportFailure.NotAnImage, fake.Failure);
        Assert.Null(fake.RelativePath);
        Assert.Equal(ImageImportFailure.Unreadable, (await DocumentImages.ImportAsync(document, _dir.File("missing.png"))).Failure);
        var svg = await DocumentImages.ImportAsync(document, _dir.Write("logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"));
        Assert.Equal("images/logo.svg", svg.RelativePath);
    }
}
