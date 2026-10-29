using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;
using ImageMagick;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Images.Magick;

namespace PowerTrove.Pack.Images.Tests;

public sealed class VisualPreviewTests : IDisposable
{
    private readonly TempDirectory directory = new();
    private readonly ImageCanvasActionTests.RealPngCodec codec = new();
    public void Dispose() => directory.Dispose();
    private IAction Action(string id, IFaceDetector? detector = null) => new ImagesPack(new CompositeImageEngine([]),
        FileActionServices.CreatePhysical(new FileSignatureVerifier()), rasterCodec: codec,
        watermarker: new MagickImageWatermarker(), faceDetector: detector ?? new Detector())
        .Actions.Single(a => a.Descriptor.Id == "powertrove.image." + id);
    private string Source(string name = "source.png", BgraColor? color = null)
    {
        var raster = RasterOperations.Canvas(200, 100, color ?? new(0, 0, 255));
        raster.Bgra[3] = 0;
        if (color is null)
            for (int y = 20; y < 80; y++) for (int x = 60; x < 140; x++)
            { int i = (y * 200 + x) * 4; raster.Bgra[i] = (byte)(x % 2 == 0 ? 255 : 0); raster.Bgra[i + 2] = (byte)(x % 2 == 0 ? 0 : 255); }
        using var stream = new MemoryStream(); PngEncoder.Encode(raster, stream);
        return directory.Write(name, stream.ToArray());
    }
    private static async Task<byte[]> Pixels(ImagePreviewData frame)
    {
        Assert.True(frame.TryDecodePng(out var bytes));
        using var png = new MemoryStream(bytes);
        return (await new ImageCanvasActionTests.RealPngCodec().DecodeAsync(png, FileFormats.Png, 512 * 512, default)).Bgra32;
    }

    [Theory]
    [InlineData("crop-aspect", 100, 100)]
    [InlineData("pad", 200, 200)]
    [InlineData("grayscale", 200, 100)]
    [InlineData("optimize-png", 200, 100)]
    [InlineData("to-ico", 48, 48)]
    [InlineData("watermark-text", 200, 100)]
    [InlineData("watermark-image", 200, 100)]
    [InlineData("blur-faces", 200, 100)]
    public async Task RenderIsExplicitBoundedAndNeverWritesFiles(string id, int width, int height)
    {
        string source = Source(); string mark = Source("mark.png", new(0, 255, 0));
        var original = File.ReadAllBytes(source); var context = Snapshots.ForPaths(source); var action = Action(id);
        var values = new Dictionary<string, string> { ["Text"] = "Test", ["Image"] = mark };
        Assert.Null((await action.PreviewAsync(context, new DictionaryParameters(values), default)).ImagePreview);
        var preview = await action.PreviewAsync(context, ImagePreviewRequest.Create(values), default);
        Assert.False(preview.IsBlocked, preview.PreviewText); Assert.NotNull(preview.ImagePreview);
        Assert.Equal((width, height), (preview.ImagePreview.SourceWidth, preview.ImagePreview.SourceHeight));
        Assert.True(preview.ImagePreview.TryDecodePng(out _));
        Assert.InRange(preview.ImagePreview.PixelWidth, 1, 512); Assert.InRange(preview.ImagePreview.PixelHeight, 1, 512);
        Assert.DoesNotContain(ImagePreviewRequest.ParameterName, values.Keys);
        Assert.Equal(original, File.ReadAllBytes(source)); Assert.Empty(directory.NewFiles(source, mark));
        if (id == "grayscale")
        {
            byte[] pixels = await Pixels(preview.ImagePreview);
            for (int i = 0; i < pixels.Length; i += 4) { Assert.Equal(pixels[i], pixels[i + 1]); Assert.Equal(pixels[i], pixels[i + 2]); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteLayoutPreviewUsesNaturalOrderAndMatchesTheExport(bool grid)
    {
        string red = Source("image2.png", new(0, 0, 255)), blue = Source("image10.png", new(255, 0, 0));
        var action = Action(grid ? "collage" : "stitch"); var context = Snapshots.ForPaths(blue, red);
        var values = new Dictionary<string, string> { ["Columns"] = "2", ["CellSize"] = "100", ["Gap"] = "10", ["Fit"] = "contain" };
        Assert.Null((await action.PreviewAsync(context, new DictionaryParameters(values), default)).ImagePreview);
        var preview = await action.PreviewAsync(context, ImagePreviewRequest.Create(values), default);
        Assert.False(preview.IsBlocked, preview.PreviewText); var frame = Assert.IsType<ImagePreviewData>(preview.ImagePreview);
        Assert.Equal(grid ? (210, 100) : (410, 100), (frame.SourceWidth, frame.SourceHeight));
        Assert.Empty(directory.NewFiles(red, blue));
        byte[] pixels = await Pixels(frame); int first = (50 * frame.PixelWidth + 50) * 4;
        Assert.Equal(255, pixels[first + 2]); Assert.Equal(0, pixels[first]);
        var receipt = await action.ExecuteAsync(context, new DictionaryParameters(values), null, default);
        Assert.True(receipt.IsSuccess, receipt.Message);
        await using var output = File.OpenRead(Assert.Single(receipt.OutputPaths));
        var decoded = await codec.DecodeAsync(output, FileFormats.Png, 512 * 512, default);
        Assert.Equal(pixels, decoded.Bgra32);
    }

    [Fact]
    public async Task WatermarkPositionAndOpacityChangeTheActualRenderedPixels()
    {
        string source = Source("source.png", new(0, 0, 255)), mark = Source("mark.png", new(0, 255, 0));
        var action = Action("watermark-image"); var context = Snapshots.ForPaths(source);
        var values = new Dictionary<string, string> { ["Image"] = mark, ["Position"] = "top-left", ["Opacity"] = "100", ["WidthPercent"] = "25" };
        var first = await action.PreviewAsync(context, ImagePreviewRequest.Create(values), default);
        values["Position"] = "bottom-right"; values["Opacity"] = "10";
        var second = await action.PreviewAsync(context, ImagePreviewRequest.Create(values), default);
        Assert.False(first.IsBlocked, first.PreviewText); Assert.False(second.IsBlocked, second.PreviewText);
        byte[] a = await Pixels(first.ImagePreview!), b = await Pixels(second.ImagePreview!);
        Assert.NotEqual(a, b); int top = (10 * 200 + 10) * 4;
        Assert.True(a[top + 1] > b[top + 1]); Assert.Empty(directory.NewFiles(source, mark));
    }

    [Fact]
    public async Task FacePreviewMatchesPixelationAndWarnsWhenNoFacesAreFound()
    {
        string source = Source(); var context = Snapshots.ForPaths(source); var action = Action("blur-faces");
        var preview = await action.PreviewAsync(context, ImagePreviewRequest.Create(new Dictionary<string, string>()), default);
        byte[] pixels = await Pixels(preview.ImagePreview!);
        var receipt = await action.ExecuteAsync(context, new EmptyParameters(), null, default);
        Assert.True(receipt.IsSuccess, receipt.Message);
        await using var stream = File.OpenRead(Assert.Single(receipt.OutputPaths));
        var exported = (await codec.DecodeAsync(stream, FileFormats.Png, 512 * 512, default)).Bgra32;
        // Thumbnail interpolation canonicalizes hidden RGB. Compare displayed pixels, including alpha.
        for (int i = 0; i < exported.Length; i += 4) if (exported[i + 3] == 0) exported[i] = exported[i + 1] = exported[i + 2] = 0;
        Assert.Equal(pixels, exported);
        Assert.InRange(pixels[(40 * 200 + 80) * 4], 1, 254);
        var noFaces = await Action("blur-faces", new Detector(false)).PreviewAsync(context, ImagePreviewRequest.Create(new Dictionary<string, string>()), default);
        Assert.NotNull(noFaces.ImagePreview); Assert.Contains(noFaces.Warnings, w => w.Contains("No faces", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InvalidInputAndCancellationNeverExposeAStaleFrame()
    {
        var action = Action("grayscale"); string bad = directory.Write("bad.png", "invalid input");
        var preview = await action.PreviewAsync(Snapshots.ForPaths(bad), ImagePreviewRequest.Create(new Dictionary<string, string>()), default);
        Assert.True(preview.IsBlocked); Assert.Null(preview.ImagePreview);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await action.PreviewAsync(Snapshots.ForPaths(Source()), ImagePreviewRequest.Create(new Dictionary<string, string>()), cts.Token));
    }
    private sealed class Detector(bool found = true) : IFaceDetector
    {
        public bool IsAvailable => true;
        public Task<IReadOnlyList<RasterRect>> DetectAsync(ImageRaster raster, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RasterRect>>(found ? [new(60, 20, 80, 60)] : []);
    }
}
