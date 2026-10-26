using System.Text;
using ContextBar.Infrastructure.Files;
using ContextBar.Pack.Images.Magick;
using ContextBar.Pack.Images;
using ImageMagick;

namespace ContextBar.Infrastructure.Tests;

public sealed class FileSignatureVerifierTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private static readonly FileInputFilter Images = new("image files", new[] { ".png", ".jpg" }, new[] { FileFormats.Png, FileFormats.Jpeg });

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task AcceptsMatchingContent()
    {
        using var image = new MagickImage(MagickColors.Red, 2, 2) { Format = MagickFormat.Png };
        string path = _dir.Write("a.png", image.ToByteArray());

        var result = await new FileSignatureVerifier().VerifyAsync(path, Images, CancellationToken.None);

        Assert.True(result.IsAccepted);
        Assert.Equal(FileFormats.Png, result.DetectedFormat);
    }

    [Fact]
    public async Task RejectsRenamedFiles()
    {
        string path = _dir.Write("invoice.jpg", "MZ\u0090\u0000 executable pretending to be a photo");

        var result = await new FileSignatureVerifier().VerifyAsync(path, Images, CancellationToken.None);

        Assert.False(result.IsAccepted);
        Assert.Contains("not a recognized", result.Reason);
    }

    [Fact]
    public async Task UnreadableFile_IsRejectedWithoutThrowing()
    {
        var result = await new FileSignatureVerifier().VerifyAsync(_dir.File("missing.png"), Images, CancellationToken.None);
        Assert.False(result.IsAccepted);
    }

    [Fact]
    public async Task Verifier_RejectsRenamedFiles_InGerman()
    {
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");
            string path = _dir.Write("invoice.jpg", "MZ\u0090\u0000");
            var result = await new FileSignatureVerifier().VerifyAsync(path, Images, CancellationToken.None);

            Assert.False(result.IsAccepted);
            Assert.Contains("kein erkanntes", result.Reason);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = original;
        }
    }
}

public class MagickImageEngineTests
{
    private static byte[] Image(MagickFormat format, uint width = 40, uint height = 20, bool alpha = false)
    {
        using var image = new MagickImage(alpha ? MagickColors.Transparent : MagickColors.Green, width, height) { Format = format };
        return image.ToByteArray();
    }

    private static async Task<MagickImage> Transform(byte[] input, ImageTransformRequest request)
    {
        using var source = new MemoryStream(input);
        var destination = new MemoryStream();
        await new MagickImageEngine().TransformAsync(source, destination, request, CancellationToken.None);
        destination.Position = 0;
        return new MagickImage(destination);
    }

    [Theory]
    [InlineData(FileFormats.Png, FileFormats.WebP, MagickFormat.WebP)]
    [InlineData(FileFormats.Jpeg, FileFormats.Png, MagickFormat.Png)]
    [InlineData(FileFormats.Png, FileFormats.Jpeg, MagickFormat.Jpeg)]
    public async Task Converts(string source, string target, MagickFormat expected)
    {
        var input = Image(source == FileFormats.Png ? MagickFormat.Png : MagickFormat.Jpeg);
        using var result = await Transform(input, new ImageTransformRequest(source, target, 80, null, null, false, true));
        Assert.Equal(expected, result.Format);
    }

    [Fact]
    public async Task TransparentPngToJpeg_IsFlattenedOntoWhite()
    {
        using var result = await Transform(Image(MagickFormat.Png, alpha: true), new ImageTransformRequest(FileFormats.Png, FileFormats.Jpeg, 90, null, null, false, true));
        var pixel = result.GetPixels().GetPixel(0, 0).ToColor()!;
        Assert.True(pixel.R > 240 && pixel.G > 240 && pixel.B > 240);
    }

    [Fact]
    public async Task DecoderIsPinnedToVerifiedFormat()
    {
        // Content verified as PNG must not be re-interpreted (for example as SVG or MSL) by extension or sniffing.
        byte[] svg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'/>");
        await Assert.ThrowsAsync<FileProcessingException>(() =>
            Transform(svg, new ImageTransformRequest(FileFormats.Png, FileFormats.Jpeg, 80, null, null, false, true)));
    }

    [Fact]
    public async Task PreservesMetadataWhenRequested()
    {
        using var image = new MagickImage(MagickColors.Green, 10, 10) { Format = MagickFormat.Jpeg };
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "KeepMe");
        image.SetProfile(exif);

        using var result = await Transform(image.ToByteArray(), new ImageTransformRequest(FileFormats.Jpeg, FileFormats.Jpeg, 0, 5, 5, false, StripMetadata: false));

        Assert.Equal("KeepMe", result.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
    }
}
