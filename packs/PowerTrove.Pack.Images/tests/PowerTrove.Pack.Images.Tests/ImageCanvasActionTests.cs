using FileKit.Imaging;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;
using ImageMagick;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Images.Magick;

namespace PowerTrove.Pack.Images.Tests;

public sealed class ImageCanvasActionTests : IDisposable
{
    private readonly TempDirectory directory = new();
    public void Dispose() => directory.Dispose();
    private IAction Action(IImageRasterCodec? codec = null) => new ImagesPack(new CompositeImageEngine([]),
        FileActionServices.CreatePhysical(new FileSignatureVerifier()), rasterCodec: codec ?? new RealPngCodec(), svgRenderer: new MagickSvgRenderer())
        .Actions.Single(a => a.Descriptor.Id == "powertrove.image.edit");
    private string Png(string name, int w = 8, int h = 8)
    {
        using var stream = new MemoryStream(); PngEncoder.Encode(RasterOperations.Canvas(w, h, new(0, 0, 255)), stream);
        return directory.Write(name, stream.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackProvidesABoundedPreviewAndExportsExactCenteredCanvasWithoutChangingTheSource(bool svg)
    {
        string file = svg ? directory.Write("source.svg", "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 128 128'><rect width='128' height='128' fill='red'/></svg>") : Png("source.png", 128, 128);
        byte[] original = File.ReadAllBytes(file);
        var action = Action(); var context = Snapshots.ForPaths(file);
        var options = Snapshots.Parameters(("CanvasWidth", "1280"), ("CanvasHeight", "640"));
        var preview = await action.PreviewAsync(context, options, default);
        Assert.False(preview.IsBlocked, preview.PreviewText);
        Assert.NotNull(preview.ImagePreview); Assert.True(preview.ImagePreview.TryDecodePng(out _));
        Assert.Equal((128, 128), (preview.ImagePreview.SourceWidth, preview.ImagePreview.SourceHeight));
        Assert.DoesNotContain(preview.ImagePreview.PngBase64, preview.ToString());
        string existing = directory.Write("source (edited).png", "keep existing output");
        var receipt = await action.ExecuteAsync(context, options, null, default);
        Assert.True(receipt.IsSuccess, receipt.Message);
        string output = Assert.Single(receipt.OutputPaths); Assert.NotEqual(existing, output);
        using var result = new MagickImage(output); Assert.Equal((1280u, 640u), (result.Width, result.Height));
        using var pixels = result.GetPixels();
        Assert.Equal(0, pixels.GetPixel(100, 320).ToColor()!.A);
        Assert.True(pixels.GetPixel(640, 320).ToColor()!.A > 0);
        Assert.Equal(0, pixels.GetPixel(1100, 320).ToColor()!.A);
        Assert.Equal(original, File.ReadAllBytes(file)); Assert.Equal("keep existing output", File.ReadAllText(existing));
    }

    [Fact]
    public async Task RotationAndPositionUseTheSameGeometryAndWarnAboutCanvasClipping()
    {
        var action = Action(); string path = Png("source.png", 20, 10); var context = Snapshots.ForPaths(path);
        var options = Snapshots.Parameters(("CanvasWidth", "10"), ("CanvasHeight", "20"), ("ImageWidth", "20"), ("ImageHeight", "10"), ("Angle", "90"));
        Assert.DoesNotContain((await action.PreviewAsync(context, options, default)).Warnings, w => w.Contains("outside"));
        var receipt = await action.ExecuteAsync(context, options, null, default);
        Assert.True(receipt.IsSuccess, receipt.Message);
        using var image = new MagickImage(Assert.Single(receipt.OutputPaths)); Assert.Equal((10u, 20u), (image.Width, image.Height));
        var clipped = Snapshots.Parameters(("CanvasWidth", "10"), ("CanvasHeight", "10"), ("ImageWidth", "20"), ("ImageHeight", "10"), ("OffsetX", "10"));
        Assert.Contains((await action.PreviewAsync(context, clipped, default)).Warnings, w => w.Contains("outside"));
    }

    [Fact]
    public async Task UnsupportedActiveSvgAndMultiSelectionNeverProduceOutputs()
    {
        string unsafeSvg = directory.Write("unsafe.svg", "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><image href='https://example.test/image'/></svg>");
        var action = Action();
        Assert.True((await action.PreviewAsync(Snapshots.ForPaths(unsafeSvg), new EmptyParameters(), default)).IsBlocked);
        Assert.Empty((await action.ExecuteAsync(Snapshots.ForPaths(unsafeSvg), new EmptyParameters(), null, default)).OutputPaths);
        var multiple = Snapshots.ForPaths(Png("one.png"), Png("two.png"));
        Assert.False((await action.MatchAsync(multiple, default)).IsAvailable);
        Assert.True((await action.PreviewAsync(multiple, new EmptyParameters(), default)).IsBlocked);
        Assert.Empty((await action.ExecuteAsync(multiple, new EmptyParameters(), null, default)).OutputPaths);
    }

    [Fact]
    public async Task RasterDecodeUsesTheProbedFormatRatherThanTheFileExtension()
    {
        var context = Snapshots.ForPaths(Png("mislabeled.jpg"));
        Assert.False((await Action().PreviewAsync(context, new EmptyParameters(), default)).IsBlocked);
        Assert.True((await Action().ExecuteAsync(context, new EmptyParameters(), null, default)).IsSuccess);
    }

    internal sealed class RealPngCodec : IImageRasterCodec
    {
        public bool CanDecode(string format) => format == FileFormats.Png;
        public bool CanEncode(string format) => format == FileFormats.Png;
        public Task<ImageRaster> DecodeAsync(Stream source, string format, long maxPixels, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var image = new MagickImage(source, new MagickReadSettings { Format = MagickFormat.Png }); image.AutoOrient();
            int w = (int)image.Width, h = (int)image.Height;
            if ((long)w * h > maxPixels) throw new InvalidDataException();
            var bytes = new byte[w * h * 4]; using var pixels = image.GetPixels();
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            { var color = pixels.GetPixel(x, y).ToColor()!; int i = (y * w + x) * 4; bytes[i] = color.B; bytes[i + 1] = color.G; bytes[i + 2] = color.R; bytes[i + 3] = color.A; }
            return Task.FromResult(new ImageRaster(w, h, bytes));
        }
        public Task EncodeAsync(ImageRaster raster, Stream destination, string format, int quality, CancellationToken ct)
        { PngEncoder.Encode(new RasterImage(raster.Width, raster.Height, raster.Bgra32), destination, ct: ct); return Task.CompletedTask; }
    }
}
