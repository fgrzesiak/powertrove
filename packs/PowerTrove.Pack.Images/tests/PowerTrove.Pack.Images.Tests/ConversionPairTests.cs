using ImageMagick;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Images.Magick;

namespace PowerTrove.Pack.Images.Tests;

public sealed class ConversionPairTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();

    [Theory]
    [InlineData(MagickFormat.WebP, "webp", "png", MagickFormat.Png)]
    [InlineData(MagickFormat.WebP, "webp", "jpeg", MagickFormat.Jpeg)]
    [InlineData(MagickFormat.Avif, "avif", "jpeg", MagickFormat.Jpeg)]
    [InlineData(MagickFormat.Avif, "avif", "png", MagickFormat.Png)]
    [InlineData(MagickFormat.Png, "png", "jpeg", MagickFormat.Jpeg)]
    [InlineData(MagickFormat.Jpeg, "jpg", "png", MagickFormat.Png)]
    [InlineData(MagickFormat.Png, "png", "webp", MagickFormat.WebP)]
    [InlineData(MagickFormat.Jpeg, "jpg", "webp", MagickFormat.WebP)]
    [InlineData(MagickFormat.Bmp, "bmp", "jpeg", MagickFormat.Jpeg)]
    public async Task DocumentedPairs_PassSignatureProbeDecodeAndSafeOutput(
        MagickFormat sourceFormat, string extension, string target, MagickFormat expected)
    {
        using var generated = new MagickImage(MagickColors.CornflowerBlue, 24, 16) { Format = sourceFormat };
        byte[] original = generated.ToByteArray();
        string path = _directory.Write("source." + extension, original);
        var services = FileActionServices.CreatePhysical(new FileSignatureVerifier());
        var pack = new ImagesPack(new CompositeImageEngine([new MagickImageEngine()]), services);
        var action = pack.Actions.Single(a => a.Descriptor.Id == "powertrove.image.convert-" + target);
        var context = Snapshots.ForPaths(path);
        Assert.True((await action.MatchAsync(context, default)).IsAvailable);
        Assert.False((await action.PreviewAsync(context, new EmptyParameters(), default)).IsBlocked);
        var receipt = await action.ExecuteAsync(context, new EmptyParameters(), null, default);
        Assert.True(receipt.IsSuccess, receipt.Message);
        using var decoded = new MagickImage(Assert.Single(receipt.OutputPaths));
        Assert.Equal(expected, decoded.Format);
        Assert.Equal((24u, 16u), (decoded.Width, decoded.Height));
        Assert.Equal(original, File.ReadAllBytes(path));
        var again = await action.ExecuteAsync(context, new EmptyParameters(), null, default);
        Assert.True(again.IsSuccess, again.Message);
        Assert.NotEqual(receipt.OutputPaths.Single(), again.OutputPaths.Single());
    }
}
