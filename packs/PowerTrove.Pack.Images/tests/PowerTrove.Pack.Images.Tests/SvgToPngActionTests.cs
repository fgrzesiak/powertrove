using ImageMagick;
using PowerTrove.Pack.Images.Magick;

namespace PowerTrove.Pack.Images.Tests;

public sealed class SvgToPngActionTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    private static IAction Action() => new ImagesPack(new CompositeImageEngine([]), FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()), svgRenderer: new MagickSvgRenderer())
        .Actions.Single(a => a.Descriptor.Id == "powertrove.image.svg-to-png");
    [Fact]
    public async Task RealRendererCreatesTransparentPngBatchWithRequestedBoundsAndPreservesInputs()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 40 20'><rect x='10' y='5' width='20' height='10' fill='red'/></svg>";
        string first = _directory.Write("a.svg", svg), second = _directory.Write("b.svg", svg);
        var action = Action(); var context = Snapshots.ForPaths(first, second); var options = Snapshots.Parameters(("Width", "200"), ("Height", "200"));
        Assert.True((await action.MatchAsync(context, default)).IsAvailable);
        Assert.False((await action.PreviewAsync(context, options, default)).IsBlocked);
        var receipt = await action.ExecuteAsync(context, options, null, default);
        Assert.True(receipt.IsSuccess, receipt.Message); Assert.Equal(2, receipt.OutputPaths.Count);
        foreach (string path in receipt.OutputPaths)
        {
            using var image = new MagickImage(path);
            Assert.Equal(MagickFormat.Png, image.Format); Assert.Equal((200u, 100u), (image.Width, image.Height)); Assert.True(image.HasAlpha);
            Assert.Equal(0, image.GetPixels().GetPixel(0, 0).ToColor()!.A);
        }
        Assert.Equal(svg, File.ReadAllText(first));
    }
    [Theory]
    [InlineData("none")]
    [InlineData("xMinYMin slice")]
    public async Task ExactCanvasCentersSquareArtworkWithoutStretchingOrCropping(string sourceAlignment)
    {
        // Source alignment must not stretch or crop the artwork in exact-canvas mode.
        string svg = $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='10 20 100 100' preserveAspectRatio='{sourceAlignment}'><rect x='10' y='20' width='100' height='100' fill='red'/></svg>";
        string path = _directory.Write("square.svg", svg);
        var options = Snapshots.Parameters(("Width", "1280"), ("Height", "640"), ("ExactCanvas", "true"));
        var action = Action();
        var context = Snapshots.ForPaths(path);
        var preview = await action.PreviewAsync(context, options, default);
        Assert.False(preview.IsBlocked);
        Assert.Contains("1280", preview.PreviewText);
        Assert.Contains("640", preview.PreviewText);
        var receipt = await action.ExecuteAsync(context, options, null, default);
        Assert.True(receipt.IsSuccess, receipt.Message);
        using var image = new MagickImage(Assert.Single(receipt.OutputPaths));
        Assert.Equal((1280u, 640u), (image.Width, image.Height));
        Assert.True(image.HasAlpha);
        using var pixels = image.GetPixels();
        Assert.Equal(0, pixels.GetPixel(319, 320).ToColor()!.A);
        Assert.Equal(0, pixels.GetPixel(960, 320).ToColor()!.A);
        Assert.True(pixels.GetPixel(321, 320).ToColor()!.A > 0);
        Assert.True(pixels.GetPixel(958, 320).ToColor()!.A > 0);
        Assert.True(pixels.GetPixel(640, 0).ToColor()!.A > 0);
        Assert.True(pixels.GetPixel(640, 639).ToColor()!.A > 0);
        Assert.Equal(svg, File.ReadAllText(path));
    }

    [Fact]
    public async Task ExactPortraitCanvasCentersWideArtworkWithTransparentTopAndBottom()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='200px' height='100px'><rect width='200' height='100' fill='blue'/></svg>";
        string path = _directory.Write("wide.svg", svg);
        var options = Snapshots.Parameters(("Width", "200"), ("Height", "400"), ("ExactCanvas", "true"));
        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(path), options, null, default);
        Assert.True(receipt.IsSuccess, receipt.Message);
        using var image = new MagickImage(Assert.Single(receipt.OutputPaths));
        Assert.Equal((200u, 400u), (image.Width, image.Height));
        using var pixels = image.GetPixels();
        Assert.Equal(0, pixels.GetPixel(100, 149).ToColor()!.A);
        Assert.Equal(0, pixels.GetPixel(100, 250).ToColor()!.A);
        Assert.True(pixels.GetPixel(100, 151).ToColor()!.A > 0);
        Assert.True(pixels.GetPixel(100, 248).ToColor()!.A > 0);
        Assert.True(pixels.GetPixel(0, 200).ToColor()!.A > 0);
        Assert.True(pixels.GetPixel(199, 200).ToColor()!.A > 0);
    }

    [Fact]
    public async Task ActiveSvgBlocksPreviewAndCreatesNoOutput()
    {
        string path = _directory.Write("unsafe.svg", "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><image href='https://example.test'/></svg>");
        var action = Action(); Assert.True((await action.PreviewAsync(Snapshots.ForPaths(path), new EmptyParameters(), default)).IsBlocked);
        var result = await action.ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, default);
        Assert.False(result.IsSuccess); Assert.Empty(result.OutputPaths); Assert.Empty(_directory.NewFiles(path));
    }
}
