using System.Buffers.Binary;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Images.Magick;
using PowerTrove.Pack.Images;
using FileKit.Imaging;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;
using ImageMagick;

namespace PowerTrove.Pack.Images.Tests;

public sealed class RecordingRasterCodec : IImageRasterCodec
{
    public List<(int Width, int Height)> Reads { get; } = new();
    public ImageRaster? LastEncoded { get; private set; }
    public string? LastFormat { get; private set; }
    public bool CanDecode(string f) => true;
    public bool CanEncode(string f) => true;
    public async Task<ImageRaster> DecodeAsync(Stream source, string format, long maxPixels, CancellationToken ct)
    {
        var info = await ImageProbe.ProbeAsync(source, ct); var (w, h) = info.DisplaySize; Reads.Add((w, h));
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(i / 4 + 1); pixels[i + 1] = 40; pixels[i + 2] = 200; pixels[i + 3] = 255; }
        return new(w, h, pixels);
    }
    public Task EncodeAsync(ImageRaster raster, Stream destination, string format, int quality, CancellationToken ct)
    {
        LastEncoded = raster; LastFormat = format; PngEncoder.Encode(new RasterImage(raster.Width, raster.Height, raster.Bgra32), destination, ct: ct); return Task.CompletedTask;
    }
}

public class RasterActionTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly RecordingRasterCodec _codec = new();
    public void Dispose() => _dir.Dispose();
    private static FileActionServices Services() => FileActionServices.CreatePhysical(new FileSignatureVerifier());
    private ImagesPack Pack() => new(new CompositeImageEngine(new IImageEngine[] { new MagickImageEngine() }), Services(), rasterCodec: _codec);
    private IAction Action(string id) => Pack().Actions.Single(a => a.Descriptor.Id == "contextbar.image." + id);
    private string Image(string name, int w = 3, int h = 2, bool alpha = true)
    {
        using var png = new MemoryStream(); PngEncoder.Encode(RasterOperations.Canvas(w, h, new BgraColor(20, 40, 80)), png, includeAlpha: alpha);
        return _dir.Write(name, png.ToArray());
    }
    [Theory]
    [InlineData(5, 3, 1, "left", 0, 0, 3, 3)]
    [InlineData(5, 3, 1, "right", 2, 0, 3, 3)]
    [InlineData(3, 5, 1, "bottom", 0, 2, 3, 3)]
    [InlineData(3, 5, 1, "top", 0, 0, 3, 3)]
    [InlineData(7, 3, 1.5, "center", 1, 0, 4, 3)]
    public void CropRectangle_HandlesAnchorsAndOddDimensions(int w, int h, double ratio, string anchor, int x, int y, int cw, int ch) =>
        Assert.Equal(new RasterRect(x, y, cw, ch), AspectCrop.Calculate(w, h, ratio, anchor));
    [Fact]
    public async Task Crop_ProducesExpectedPixelsAndCollisionSafeNames()
    {
        string path = Image("private.png"); _dir.Write("private (cropped).png", "keep");
        var action = Action("crop-aspect");
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), Snapshots.Parameters(("Ratio", "1:1"), ("Anchor", "right")), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); Assert.Equal((2, 2), (_codec.LastEncoded!.Width, _codec.LastEncoded.Height));
        Assert.Equal(new byte[] { 2, 3, 5, 6 }, _codec.LastEncoded.Bgra32.Where((_, i) => i % 4 == 0));
        Assert.NotEqual(_dir.File("private (cropped).png"), Assert.Single(receipt.OutputPaths));
        Assert.Equal("keep", File.ReadAllText(_dir.File("private (cropped).png")));
        Assert.DoesNotContain("private", receipt.Message); Assert.True(File.Exists(path));
    }
    [Theory]
    [InlineData("0:1")]
    [InlineData("NaN:1")]
    [InlineData("1:0")]
    [InlineData("bad")]
    public async Task InvalidCustomRatio_BlocksPreview(string ratio)
    {
        var preview = await Action("crop-aspect").PreviewAsync(Snapshots.ForPaths(Image("a.png")), Snapshots.Parameters(("Ratio", "custom"), ("CustomRatio", ratio)), CancellationToken.None);
        Assert.True(preview.IsBlocked); Assert.Empty(_codec.Reads);
    }
    [Fact]
    public async Task Pad_SquareAndBorderProduceCorrectCanvasAndTransparency()
    {
        var action = Action("pad"); var snapshot = Snapshots.ForPaths(Image("a.png", 2, 3));
        var receipt = await action.ExecuteAsync(snapshot, Snapshots.Parameters(("Mode", "square"), ("Transparent", "true")), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); Assert.Equal((3, 3), (_codec.LastEncoded!.Width, _codec.LastEncoded.Height));
        Assert.Equal(0, _codec.LastEncoded.Bgra32[2 * 4 + 3]);
        await action.ExecuteAsync(snapshot, Snapshots.Parameters(("Mode", "border"), ("BorderPx", "2"), ("Color", "#112233")), null, CancellationToken.None);
        Assert.Equal((6, 7), (_codec.LastEncoded!.Width, _codec.LastEncoded.Height));
        Assert.Equal(new byte[] { 0x33, 0x22, 0x11, 255 }, _codec.LastEncoded.Bgra32[..4]);
    }
    [Fact]
    public async Task Pad_JpegWarnsAndUsesWhiteForTransparentRequest()
    {
        using var image = new MagickImage(MagickColors.Blue, 2, 3) { Format = MagickFormat.Jpeg }; string path = _dir.Write("a.jpg", image.ToByteArray());
        var options = Snapshots.Parameters(("Transparent", "true")); var snapshot = Snapshots.ForPaths(path);
        var preview = await Action("pad").PreviewAsync(snapshot, options, CancellationToken.None);
        Assert.Contains(preview.Warnings, w => w.Contains("white"));
        await Action("pad").ExecuteAsync(snapshot, options, null, CancellationToken.None);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, _codec.LastEncoded!.Bgra32.AsSpan(8, 4).ToArray());
    }
    [Fact]
    public async Task Grayscale_KeepsFormatAndAlpha()
    {
        var receipt = await Action("grayscale").ExecuteAsync(Snapshots.ForPaths(Image("gray.png")), new EmptyParameters(), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); Assert.Equal(FileFormats.Png, _codec.LastFormat);
        for (int i = 0; i < _codec.LastEncoded!.Bgra32.Length; i += 4)
        { Assert.Equal(_codec.LastEncoded.Bgra32[i], _codec.LastEncoded.Bgra32[i + 1]); Assert.Equal(_codec.LastEncoded.Bgra32[i], _codec.LastEncoded.Bgra32[i + 2]); Assert.Equal(255, _codec.LastEncoded.Bgra32[i + 3]); }
    }
    [Fact]
    public async Task OversizedHeader_IsRejectedBeforeDecoderRuns()
    {
        string path = Image("flood.png"); byte[] bytes = File.ReadAllBytes(path); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), 30000); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), 30000); File.WriteAllBytes(path, bytes);
        var receipt = await Action("grayscale").ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, CancellationToken.None);
        Assert.False(receipt.IsSuccess); Assert.Empty(_codec.Reads); Assert.Single(receipt.Failures);
    }
    [Fact]
    public async Task OptimizePng_SkipsAFileThatIsAlreadySmaller()
    {
        var receipt = await Action("optimize-png").ExecuteAsync(Snapshots.ForPaths(Image("a.png", 1, 1)), new EmptyParameters(), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); Assert.Empty(receipt.OutputPaths); Assert.Empty(receipt.Failures); Assert.Contains("no changes", receipt.Message);
    }
    [Fact]
    public async Task Icon_PadsNonSquareImagesAndWritesEveryRequestedSize()
    {
        var receipt = await Action("to-ico").ExecuteAsync(Snapshots.ForPaths(Image("icon.png", 4, 2)), Snapshots.Parameters(("Sizes", "windows")), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); byte[] bytes = File.ReadAllBytes(Assert.Single(receipt.OutputPaths));
        Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(16, bytes[6]); // first directory entry: 16 px (stored as a DIB, 256 px as PNG)
        using var frames = new MagickImageCollection(bytes, new MagickReadSettings { Format = MagickFormat.Ico });
        var small = frames.Single(f => f.Width == 16);
        using var iconPixels = small.GetPixels();
        Assert.True(small.HasAlpha); Assert.Equal(0, iconPixels.GetPixel(0, 0).ToArray()[3]);
        Assert.Contains(frames, f => f.Width == 256);
    }
    [Theory]
    [InlineData("tiff", MagickFormat.Tiff)]
    [InlineData("bmp", MagickFormat.Bmp)]
    [InlineData("gif", MagickFormat.Gif)]
    [InlineData("ico", MagickFormat.Ico)]
    public async Task OtherFormats_ProduceDecodableOutputs(string format, MagickFormat expected)
    {
        var action = Action("convert-other"); var snapshot = Snapshots.ForPaths(Image("source.png")); var options = Snapshots.Parameters(("Format", format));
        var receipt = await action.ExecuteAsync(snapshot, options, null, CancellationToken.None);
        Assert.True(receipt.IsSuccess);
        string output = Assert.Single(receipt.OutputPaths);
        if (expected == MagickFormat.Ico)
        {
            // ICO with an embedded PNG entry: Magick reports the entry's format (PNG), so check the container header.
            Assert.Equal(new byte[] { 0, 0, 1, 0 }, File.ReadAllBytes(output).Take(4).ToArray());
            using var icon = new MagickImage(output); Assert.True(icon.Width > 0);
        }
        else
        {
            using var result = new MagickImage(output); Assert.Equal(expected, result.Format);
        }
        if (format == "gif") Assert.Contains((await action.PreviewAsync(snapshot, options, CancellationToken.None)).Warnings, w => w.Contains("first frame"));
        Assert.Equal(-20, action.Descriptor.ExplicitWeight);
    }
    [Fact]
    public async Task Info_IsReadOnlyAndSummarizesTwentyInputsWithoutNames()
    {
        var files = Enumerable.Range(1, 20).Select(n => Image("secret-" + n + ".png", 2, 1)).ToArray();
        var action = Action("info"); var receipt = await action.ExecuteAsync(Snapshots.ForPaths(files), new EmptyParameters(), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); Assert.Empty(receipt.OutputPaths); Assert.DoesNotContain("secret", receipt.Message);
        Assert.Contains("2 × 1", receipt.Message); Assert.Contains("DPI", receipt.Message); Assert.Contains("location: No", receipt.Message); Assert.Contains("frames: 1", receipt.Message);
        Assert.Equal(20, receipt.Message!.Split(Environment.NewLine).Length);
        Assert.False((await action.MatchAsync(Snapshots.ForPaths(files.Append(Image("extra.png")).ToArray()), CancellationToken.None)).IsAvailable);
    }
    [Fact]
    public async Task MissingRasterCodec_LeavesLegacyActionsAvailableAndNewActionsTruthful()
    {
        var pack = new ImagesPack(new CompositeImageEngine(new IImageEngine[] { new MagickImageEngine() }), Services());
        var snapshot = Snapshots.ForPaths(Image("a.png"));
        Assert.True((await pack.Actions.Single(a => a.Descriptor.Id == "contextbar.image.convert-png").MatchAsync(snapshot, CancellationToken.None)).IsAvailable);
        Assert.False((await pack.Actions.Single(a => a.Descriptor.Id == "contextbar.image.crop-aspect").MatchAsync(snapshot, CancellationToken.None)).IsAvailable);
    }
    [Fact]
    public async Task CancellationDuringEncoding_RemovesTemporaryOutputAndPreservesSource()
    {
        string path = Image("source.png"); byte[] before = File.ReadAllBytes(path); using var cts = new CancellationTokenSource();
        var codec = new CancelDuringEncodeCodec(cts);
        var action = new RasterFileAction(RasterOperation.Grayscale, Services(), codec, new CompositeImageEngine(Array.Empty<IImageEngine>()));
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, cts.Token);
        Assert.True(receipt.IsCanceled); Assert.Empty(receipt.OutputPaths); Assert.Empty(_dir.NewFiles(path)); Assert.Equal(before, File.ReadAllBytes(path));
    }
    private sealed class CancelDuringEncodeCodec(CancellationTokenSource cts) : IImageRasterCodec
    {
        private readonly RecordingRasterCodec _decoder = new();
        public bool CanDecode(string f) => true; public bool CanEncode(string f) => true;
        public Task<ImageRaster> DecodeAsync(Stream source, string f, long max, CancellationToken ct) => _decoder.DecodeAsync(source, f, max, ct);
        public Task EncodeAsync(ImageRaster raster, Stream destination, string f, int q, CancellationToken ct)
        {
            destination.Write(new byte[] { 1, 2, 3 }); cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }
    }
}

public abstract class RasterContractTests : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _dir = new();
    protected abstract string Id { get; }
    protected override IAction CreateAction() => new ImagesPack(new CompositeImageEngine(new IImageEngine[] { new MagickImageEngine() }), FileActionServices.CreatePhysical(new FileSignatureVerifier()), rasterCodec: new RecordingRasterCodec()).Actions.Single(a => a.Descriptor.Id == "contextbar.image." + Id);
    protected override ContextSnapshot CreateValidSnapshot()
    {
        using var png = new MemoryStream(); PngEncoder.Encode(RasterOperations.Canvas(3, 2, new BgraColor(30, 50, 70)), png);
        return Snapshots.ForPaths(_dir.Write("a.png", png.ToArray()), _dir.Write("b.png", png.ToArray()));
    }
    public void Dispose() => _dir.Dispose();
}
public sealed class CropContractTests : RasterContractTests { protected override string Id => "crop-aspect"; }
public sealed class PadContractTests : RasterContractTests { protected override string Id => "pad"; }
public sealed class GrayscaleContractTests : RasterContractTests { protected override string Id => "grayscale"; }
public sealed class OptimizePngContractTests : RasterContractTests { protected override string Id => "optimize-png"; }
public sealed class IconContractTests : RasterContractTests { protected override string Id => "to-ico"; }
public sealed class OtherFormatContractTests : RasterContractTests { protected override string Id => "convert-other"; }
public sealed class InfoContractTests : RasterContractTests { protected override string Id => "info"; }
public sealed class StitchContractTests : RasterContractTests { protected override string Id => "stitch"; }
public sealed class CollageContractTests : RasterContractTests { protected override string Id => "collage"; }
public sealed class TargetSizeContractTests : RasterContractTests { protected override string Id => "target-size"; }
