using PowerTrove.Abstractions.Testing;
using PowerTrove.Desktop;

namespace PowerTrove.Architecture.Tests;

public sealed class OcrReaderInputTests
{
    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".webp")]
    [InlineData(".tif")]
    public void SingleLocalRasterOpensTheVisualReader(string extension)
    {
        var snapshot = Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "ocr-generated" + extension));
        Assert.True(OcrReaderInput.CanOpen(OcrReaderInput.ActionId, snapshot));
        var file = ((FileSelection)snapshot.Content).Files[0];
        Assert.False(OcrReaderInput.CanOpen("powertrove.image.info", snapshot));
        Assert.False(OcrReaderInput.CanOpen(OcrReaderInput.ActionId,
            snapshot with { Content = new FileSelection([file, file]) }));
        Assert.False(OcrReaderInput.CanOpen(OcrReaderInput.ActionId,
            snapshot with { Content = new FileSelection([file with { IsAccessible = false }]) }));
        Assert.False(OcrReaderInput.CanOpen(OcrReaderInput.ActionId,
            snapshot with { Content = new FileSelection([file with { IsAvailableLocally = false }]) }));
        Assert.False(OcrReaderInput.CanOpen(OcrReaderInput.ActionId,
            snapshot with { Content = new FileSelection([file with { IsDirectory = true }]) }));
    }

    [Fact]
    public void PdfSvgAndExpiredSnapshotsKeepTheirExistingRoutes()
    {
        var image = Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "generated.png"));
        var file = ((FileSelection)image.Content).Files[0];
        Assert.False(OcrReaderInput.CanOpen(OcrReaderInput.ActionId,
            image with { Content = new FileSelection([file with { Extension = ".pdf" }]) }));
        Assert.False(OcrReaderInput.CanOpen(OcrReaderInput.ActionId,
            image with { Content = new FileSelection([file with { Extension = ".svg" }]) }));
        Assert.False(OcrReaderInput.CanOpen(OcrReaderInput.ActionId,
            image with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }));
    }
}
