using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Images.Magick;
using PowerTrove.Pack.Images;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;

namespace PowerTrove.Pack.Images.Tests;

public class ImageLayoutAndTargetTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("center")]
    [InlineData("end")]
    public void Stitch_ScalesToSmallestSideAndAccountsForGap(string align)
    {
        var layout = ImageLayouts.Stitch(new[] { new ImageSize(100, 50), new ImageSize(300, 100) }, true, 7, align);
        Assert.Equal((257, 50), (layout.Width, layout.Height));
        Assert.Equal(new ImagePlacement(150, 50, 107, 0), layout.Images[1]);
        var vertical = ImageLayouts.Stitch(new[] { new ImageSize(100, 50), new ImageSize(200, 200) }, false, 3, align);
        Assert.Equal((100, 153), (vertical.Width, vertical.Height)); Assert.Equal(53, vertical.Images[1].Y);
    }
    [Fact]
    public void Grid_AutoColumnsAndCoverContainDiffer()
    {
        var sizes = Enumerable.Repeat(new ImageSize(400, 200), 5).ToArray();
        var cover = ImageLayouts.Grid(sizes, 0, 100, 5, true); Assert.Equal((310, 205), (cover.Width, cover.Height));
        Assert.Equal(new RasterRect(100, 0, 200, 200), cover.Images[0].Crop);
        var contain = ImageLayouts.Grid(sizes, 0, 100, 5, false); Assert.Null(contain.Images[0].Crop);
        Assert.Equal(new ImagePlacement(100, 50, 0, 25), contain.Images[0]); Assert.Equal(105, contain.Images[4].X);
    }
    [Fact]
    public void Layouts_RejectCanvasBombsBeforeAllocating()
    {
        Assert.Throws<RasterException>(() => ImageLayouts.Grid(Enumerable.Repeat(new ImageSize(100, 100), 100).ToArray(), 10, 2000, 0, true));
        Assert.Throws<RasterException>(() => ImageLayouts.Stitch(Enumerable.Repeat(new ImageSize(30000, 2000), 50).ToArray(), true, 0, "center"));
        Assert.Throws<RasterException>(() => ImageLayouts.Grid(new[] { new ImageSize(10, 10) }, 0, 100, 0, false));
    }
    [Fact]
    public async Task Combine_UsesNaturalNameOrderAndContentAwareOutputExtension()
    {
        using var dir = new TempDirectory();
        string Write(string name, int width, int height, bool alpha)
        {
            using var png = new MemoryStream(); PngEncoder.Encode(RasterOperations.Canvas(width, height, new BgraColor(10, 20, 30)), png, alpha); return dir.Write(name, png.ToArray());
        }
        var codec = new RecordingRasterCodec(); var services = FileActionServices.CreatePhysical(new FileSignatureVerifier());
        var action = new CombineImagesAction(false, services, codec);
        var snapshot = Snapshots.ForPaths(Write("photo10.png", 4, 2, false), Write("photo2.png", 2, 1, false));
        var options = Snapshots.Parameters(("Gap", "1"));
        var preview = await action.PreviewAsync(snapshot, options, CancellationToken.None); Assert.EndsWith("photo2 (combined).jpg", Assert.Single(preview.ExpectedOutputs));
        var receipt = await action.ExecuteAsync(snapshot, options, null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); Assert.Equal(new[] { (2, 1), (4, 2) }, codec.Reads); Assert.Equal(FileFormats.Jpeg, codec.LastFormat);
        Assert.Equal((5, 1), (codec.LastEncoded!.Width, codec.LastEncoded.Height));
        var withAlpha = Snapshots.ForPaths(Write("alpha.png", 2, 1, true), Write("opaque.png", 2, 1, false));
        await action.ExecuteAsync(withAlpha, new EmptyParameters(), null, CancellationToken.None); Assert.Equal(FileFormats.Png, codec.LastFormat);
    }
    [Fact]
    public async Task CombinationCountLimits_AreStaticAndDoNotDecode()
    {
        using var dir = new TempDirectory(); string file = dir.Write("a.png", new byte[0]); var codec = new RecordingRasterCodec();
        var snapshot = Snapshots.ForPaths(Enumerable.Repeat(file, 101).ToArray());
        foreach (bool grid in new[] { true, false })
        {
            var action = new CombineImagesAction(grid, FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()), codec);
            Assert.False((await action.MatchAsync(snapshot, CancellationToken.None)).IsAvailable);
        }
        Assert.Empty(codec.Reads);
    }
    [Fact]
    public async Task QualitySearch_FindsHighestFittingQualityAndStopsOnPlateaus()
    {
        var result = await TargetSizeSearch.FindAsync(700, (q, s, ct) => Task.FromResult(new byte[q * 10]), CancellationToken.None);
        Assert.NotNull(result); Assert.Equal(70, result.Quality); Assert.True(result.Attempts <= 7); Assert.Equal(1d, result.Scale);
        var plateau = await TargetSizeSearch.FindAsync(100, (q, s, ct) => Task.FromResult(new byte[50]), CancellationToken.None);
        Assert.Equal(95, plateau!.Quality); Assert.Equal(7, plateau.Attempts);
    }
    [Fact]
    public async Task Search_ReducesDimensionsAndHasAHardAttemptLimit()
    {
        int calls = 0;
        var missing = await TargetSizeSearch.FindAsync(10, (q, s, ct) => { calls++; return Task.FromResult(new byte[100]); }, CancellationToken.None);
        Assert.Null(missing); Assert.InRange(calls, 1, 42);
        var resized = await TargetSizeSearch.FindAsync(100, (q, s, ct) => Task.FromResult(new byte[s < 0.8 ? 50 : 200]), CancellationToken.None);
        Assert.NotNull(resized); Assert.Equal(Math.Pow(0.85, 2), resized.Scale);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TargetSizeSearch.FindAsync(100, (q, s, ct) => throw new InvalidOperationException(), cts.Token));
    }
    [Fact]
    public async Task TargetSize_WithMagick_Reaches100KbForGeneratedNoise()
    {
        using var dir = new TempDirectory(); var pixels = new byte[2000 * 1500 * 4]; new Random(13).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        using var png = new MemoryStream(); PngEncoder.Encode(new RasterImage(2000, 1500, pixels), png, false);
        string path = dir.Write("noise.png", png.ToArray());
        var action = new TargetSizeAction(new CompositeImageEngine(new IImageEngine[] { new MagickImageEngine() }), FileActionServices.CreatePhysical(new FileSignatureVerifier()));
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), Snapshots.Parameters(("TargetKb", "100"), ("Format", "jpeg")), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess, receipt.Message); Assert.InRange(new FileInfo(Assert.Single(receipt.OutputPaths)).Length, 1, 100 * 1024);
        Assert.True(File.Exists(path));
    }
    [Fact]
    public async Task TargetSize_AlreadySmallIsSkipped()
    {
        using var dir = new TempDirectory(); using var png = new MemoryStream(); PngEncoder.Encode(RasterOperations.Canvas(2, 2, default), png);
        var action = new TargetSizeAction(new CompositeImageEngine(new IImageEngine[] { new MagickImageEngine() }), FileActionServices.CreatePhysical(new FileSignatureVerifier()));
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(dir.Write("small.png", png.ToArray())), new EmptyParameters(), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); Assert.Empty(receipt.OutputPaths); Assert.Empty(receipt.Failures);
    }
}
