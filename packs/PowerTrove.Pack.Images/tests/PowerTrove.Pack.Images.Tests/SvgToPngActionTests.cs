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
    [Fact]
    public async Task ActiveSvgBlocksPreviewAndCreatesNoOutput()
    {
        string path = _directory.Write("unsafe.svg", "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><image href='https://example.test'/></svg>");
        var action = Action(); Assert.True((await action.PreviewAsync(Snapshots.ForPaths(path), new EmptyParameters(), default)).IsBlocked);
        var result = await action.ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, default);
        Assert.False(result.IsSuccess); Assert.Empty(result.OutputPaths); Assert.Empty(_directory.NewFiles(path));
    }
}
