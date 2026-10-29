using FileKit.Imaging;
using PowerTrove.Pack.Images;

namespace PowerTrove.Pack.Images.Tests;

public class KeepFormatTests
{
    private static bool AllButHeic(string format) => format != FileFormats.Heic;

    [Theory]
    [InlineData(FileFormats.Png, false, FileFormats.Png)]
    [InlineData(FileFormats.Jpeg, false, FileFormats.Jpeg)]
    [InlineData(FileFormats.Heic, false, FileFormats.Jpeg)]
    [InlineData(FileFormats.Heic, true, FileFormats.Png)]
    public void KeepsWritableFormatsAndFallsBackForOthers(string source, bool transparency, string expected) =>
        Assert.Equal(expected, RasterSupport.KeepFormat(source, AllButHeic, transparency));

    [Fact]
    public void UnwritablePhotoFormatsBecomeJpegAndOthersPng()
    {
        Assert.Equal(FileFormats.Jpeg, RasterSupport.KeepFormat(FileFormats.Avif, _ => false));
        Assert.Equal(FileFormats.Png, RasterSupport.KeepFormat(FileFormats.Tiff, _ => false));
    }

    [Fact]
    public void WarnsOncePerFallbackFormatOnly()
    {
        static SelectedFile File(string name) => new(null, name, System.IO.Path.GetExtension(name), null, null, true, true, null);
        var files = new[] { File("a.heic"), File("b.heic"), File("c.png") };
        var warning = Assert.Single(RasterSupport.KeepFormatWarnings(files, AllButHeic));
        Assert.Contains("HEIC", warning);
        Assert.Contains("JPEG", warning);
    }

    [Theory]
    [InlineData(FileFormats.Heic, "same", FileFormats.Jpeg)]
    [InlineData(FileFormats.Heic, FileFormats.Avif, FileFormats.Avif)]
    [InlineData(FileFormats.Png, FileFormats.Heic, FileFormats.Jpeg)]
    [InlineData(FileFormats.Png, FileFormats.WebP, FileFormats.WebP)]
    public void ChosenOutputFormatWinsWhenItCanBeWritten(string source, string chosen, string expected) =>
        Assert.Equal(expected, RasterSupport.OutputFormat(source, chosen, AllButHeic));

    [Fact]
    public void UnwritableHeicLinksToTheHevcExtension()
    {
        static SelectedFile File(string name) => new(null, name, System.IO.Path.GetExtension(name), null, null, true, true, null);
        string warning = Assert.Single(RasterSupport.OutputFormatWarnings(new[] { File("a.heic") }, RasterSupport.SameFormat, AllButHeic));
        Assert.Contains("](ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7)", warning);
        Assert.Empty(RasterSupport.OutputFormatWarnings(new[] { File("a.heic") }, FileFormats.Png, AllButHeic));
    }
}
