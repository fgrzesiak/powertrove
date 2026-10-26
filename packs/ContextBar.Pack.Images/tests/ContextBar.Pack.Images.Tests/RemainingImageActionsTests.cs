using ContextBar.Abstractions.Ocr;
using ContextBar.Infrastructure.Files;
using ContextBar.Pack.Images.Magick;
using ContextBar.Pack.Images;
using FileKit.Imaging;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;
using ImageMagick;

namespace ContextBar.Pack.Images.Tests;

public sealed class StubRecognizer : ITextRecognizer
{
    public string? LastLanguage { get; private set; }
    public IReadOnlyList<string> AvailableLanguages { get; init; } = new[] { "en-US", "de-DE" };
    public Func<int, IReadOnlyList<RecognizedLine>> Result { get; init; } = _ => new[] { new RecognizedLine("PRIVATE FIRST", Array.Empty<RecognizedWord>()), new RecognizedLine("PRIVATE SECOND", Array.Empty<RecognizedWord>()) };
    public Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(int w, int h, byte[] pixels, string? language, CancellationToken ct) { ct.ThrowIfCancellationRequested(); LastLanguage = language; return Task.FromResult(Result(w)); }
}
public sealed class StubFaces : IFaceDetector
{
    public bool IsAvailable { get; init; } = true;
    public IReadOnlyList<RasterRect> Faces { get; init; } = Array.Empty<RasterRect>();
    public Task<IReadOnlyList<RasterRect>> DetectAsync(ImageRaster raster, CancellationToken ct) => Task.FromResult(Faces);
}
public sealed class StubLossless : IImageLosslessTransformer
{
    public LosslessImageRequest? Request { get; private set; }
    public async Task TransformAsync(Stream source, Stream destination, LosslessImageRequest request, CancellationToken ct) { Request = request; await source.CopyToAsync(destination, ct); }
}
public sealed class StubGifEncoder : IAnimatedGifEncoder
{
    public GifEncodingOptions? Options { get; private set; }
    public List<ImageRaster> Frames { get; } = new();
    public async Task EncodeAsync(IAsyncEnumerable<ImageRaster> frames, Stream output, GifEncodingOptions options, CancellationToken ct)
    {
        Options = options; await foreach (var frame in frames.WithCancellation(ct)) Frames.Add(frame);
        await output.WriteAsync("GIF89a"u8.ToArray(), ct);
    }
}
public sealed class StubWatermarker : IImageWatermarker
{
    public bool RejectImage { get; init; }
    public ImageWatermarkRequest? Request { get; private set; }
    public Task ValidateAsync(Stream source, string format, CancellationToken ct) => RejectImage ? Task.FromException(new FileProcessingException("Invalid test watermark.")) : Task.CompletedTask;
    public async Task ApplyAsync(Stream source, Stream? mark, Stream output, ImageWatermarkRequest request, CancellationToken ct) { Request = request; await source.CopyToAsync(output, ct); }
}

public class RemainingImageActionsTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();
    private static FileActionServices Services() => FileActionServices.CreatePhysical(new FileSignatureVerifier());
    private string Png(string name, int width = 8, int height = 4)
    {
        using var bytes = new MemoryStream(); PngEncoder.Encode(RasterOperations.Canvas(width, height, new BgraColor(10, 30, 70)), bytes); return _dir.Write(name, bytes.ToArray());
    }
    [Theory]
    [InlineData("rotate-right", OrthogonalTransform.Rotate90, 4, 8)]
    [InlineData("rotate-left", OrthogonalTransform.Rotate270, 4, 8)]
    [InlineData("rotate-180", OrthogonalTransform.Rotate180, 8, 4)]
    [InlineData("flip-horizontal", OrthogonalTransform.FlipHorizontal, 8, 4)]
    [InlineData("flip-vertical", OrthogonalTransform.FlipVertical, 8, 4)]
    public async Task RasterRotation_MatchesExactToolPixels(string id, OrthogonalTransform transform, int width, int height)
    {
        var codec = new RecordingRasterCodec(); string input = Png("source.png");
        var action = new RotationAction(id, transform, false, Services(), codec, null);
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(input), new EmptyParameters(), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); Assert.Equal((width, height), (codec.LastEncoded!.Width, codec.LastEncoded.Height));
        var original = await new RecordingRasterCodec().DecodeAsync(new MemoryStream(File.ReadAllBytes(input)), FileFormats.Png, 32, CancellationToken.None);
        var expected = ImageTransforms.Apply(new RasterImage(original.Width, original.Height, original.Bgra32), transform, CancellationToken.None);
        Assert.Equal(expected.Bgra, codec.LastEncoded.Bgra32);
    }
    [Fact]
    public async Task JpegRotation_UsesLosslessPortAndCombinesExifOrientation()
    {
        using var image = new MagickImage(MagickColors.Blue, 16, 32) { Format = MagickFormat.Jpeg, Orientation = OrientationType.RightTop };
        var exif = new ExifProfile(); exif.SetValue(ExifTag.Orientation, (ushort)6); image.SetProfile(exif); string path = _dir.Write("photo.jpg", image.ToByteArray());
        var lossless = new StubLossless(); var codec = new RecordingRasterCodec();
        var action = new RotationAction("rotate-left", OrthogonalTransform.Rotate270, false, Services(), codec, lossless);
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); Assert.Empty(codec.Reads); Assert.Equal(new LosslessImageRequest(OrthogonalTransform.Identity, 6), lossless.Request);
        Assert.Contains((await action.PreviewAsync(Snapshots.ForPaths(path), new EmptyParameters(), CancellationToken.None)).Warnings, w => w.Contains("including location"));
    }
    [Fact]
    public async Task AutoRotate_SkipsUprightInputs()
    {
        var codec = new RecordingRasterCodec(); var action = new RotationAction("auto-rotate", OrthogonalTransform.Identity, true, Services(), codec, new StubLossless());
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(Png("upright.png")), new EmptyParameters(), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); Assert.Empty(codec.Reads); Assert.Empty(receipt.OutputPaths);
    }
    [Fact]
    public async Task Ocr_ClipboardKeepsTextOutOfMessagesAndDoesNotWriteFiles()
    {
        string path = Png("private.png"); var action = new ImageOcrAction(Services(), new RecordingRasterCodec(), new StubRecognizer());
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); Assert.Equal("PRIVATE FIRST" + Environment.NewLine + "PRIVATE SECOND", receipt.ClipboardText);
        Assert.DoesNotContain("PRIVATE", receipt.Message); Assert.DoesNotContain("private", receipt.Message); Assert.Empty(receipt.OutputPaths); Assert.Empty(_dir.NewFiles(path));
        Assert.Empty((await action.PreviewAsync(Snapshots.ForPaths(path), new EmptyParameters(), CancellationToken.None)).ExpectedOutputs);
    }
    [Fact]
    public async Task Ocr_FileIsUtf8WithoutBomAndKeepsOriginal()
    {
        string path = Png("text.png"); var recognizer = new StubRecognizer { Result = _ => new[] { new RecognizedLine("Grüße 世界", Array.Empty<RecognizedWord>()) } };
        var action = new ImageOcrAction(Services(), new RecordingRasterCodec(), recognizer);
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), Snapshots.Parameters(("Output", "file"), ("Language", "de-DE")), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); string output = Assert.Single(receipt.OutputPaths); Assert.Equal(_dir.File("text.txt"), output);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("Grüße 世界"), File.ReadAllBytes(output)); Assert.Null(receipt.ClipboardText); Assert.True(File.Exists(path));
        Assert.Equal("de-DE", recognizer.LastLanguage);
    }
    [Fact]
    public async Task ConcurrentOcrInvocations_KeepClipboardPayloadsSeparate()
    {
        var recognizer = new StubRecognizer { Result = w => new[] { new RecognizedLine(w == 8 ? "FIRST" : "SECOND", Array.Empty<RecognizedWord>()) } };
        var action = new ImageOcrAction(Services(), new StatelessCodec(), recognizer);
        var result = await Task.WhenAll(action.ExecuteAsync(Snapshots.ForPaths(Png("one.png", 8, 4)), new EmptyParameters(), null, CancellationToken.None),
            action.ExecuteAsync(Snapshots.ForPaths(Png("two.png", 12, 4)), new EmptyParameters(), null, CancellationToken.None));
        Assert.Equal("FIRST", result[0].ClipboardText); Assert.Equal("SECOND", result[1].ClipboardText);
        Assert.All(result, r => { Assert.True(r.IsSuccess); Assert.DoesNotContain("FIRST", r.Message); Assert.DoesNotContain("SECOND", r.Message); });
    }
    private sealed class StatelessCodec : IImageRasterCodec
    {
        public bool CanDecode(string f) => true; public bool CanEncode(string f) => false;
        public async Task<ImageRaster> DecodeAsync(Stream source, string format, long limit, CancellationToken ct)
        {
            var info = await ImageProbe.ProbeAsync(source, ct); return new(info.Width, info.Height, RasterOperations.Canvas(info.Width, info.Height, default).Bgra);
        }
        public Task EncodeAsync(ImageRaster raster, Stream destination, string format, int quality, CancellationToken ct) => throw new InvalidOperationException();
    }
    [Fact]
    public async Task Ocr_EmptyResultsDoNotClearClipboardAndMissingLanguagesAreUnavailable()
    {
        var snapshot = Snapshots.ForPaths(Png("empty.png"));
        var empty = new ImageOcrAction(Services(), new RecordingRasterCodec(), new StubRecognizer { Result = _ => Array.Empty<RecognizedLine>() });
        var receipt = await empty.ExecuteAsync(snapshot, new EmptyParameters(), null, CancellationToken.None); Assert.True(receipt.IsSuccess); Assert.Null(receipt.ClipboardText);
        var unavailable = new ImageOcrAction(Services(), new RecordingRasterCodec(), new StubRecognizer { AvailableLanguages = Array.Empty<string>() });
        Assert.False((await unavailable.MatchAsync(snapshot, CancellationToken.None)).IsAvailable);
    }
    [Fact]
    public async Task Gif_UsesNaturalOrderCanvasAndCentisecondTiming()
    {
        var codec = new RecordingRasterCodec(); var encoder = new StubGifEncoder();
        var action = new AnimatedGifAction(Services(), codec, encoder);
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(Png("frame10.png", 16, 16), Png("frame2.png", 32, 16), Png("frame3.png", 8, 64)),
            Snapshots.Parameters(("DelayMs", "25"), ("Loop", "false"), ("MaxSize", "64")), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); Assert.Equal(new[] { (32, 16), (8, 64), (16, 16) }, codec.Reads);
        Assert.Equal(new GifEncodingOptions(32, 16, 3, false), encoder.Options); Assert.Equal(3, encoder.Frames.Count);
        Assert.All(encoder.Frames, f => Assert.Equal((32, 16), (f.Width, f.Height))); Assert.Equal(new byte[] { 255, 255, 255, 255 }, encoder.Frames[1].Bgra32[..4]);
        Assert.EndsWith("frame2 (animation).gif", Assert.Single(receipt.OutputPaths));
        var tooMany = Snapshots.ForPaths(Enumerable.Repeat(Png("one.png"), 301).ToArray()); Assert.False((await action.MatchAsync(tooMany, CancellationToken.None)).IsAvailable);
    }
    [Fact]
    public async Task Watermark_ValidatesAuxiliaryImageAndPassesOptions()
    {
        string path = Png("source.png"), mark = Png("mark.png"); var marker = new StubWatermarker();
        var action = new WatermarkAction(true, Services(), marker); var options = Snapshots.Parameters(("Image", mark), ("Opacity", "25"), ("WidthPercent", "30"), ("Position", "center"));
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), options, null, CancellationToken.None);
        Assert.True(receipt.IsSuccess); Assert.Equal("center", marker.Request!.Position); Assert.Equal(25, marker.Request.Opacity); Assert.Equal(30, marker.Request.WidthPercent);
        var rejected = new WatermarkAction(true, Services(), new StubWatermarker { RejectImage = true });
        Assert.True((await rejected.PreviewAsync(Snapshots.ForPaths(path), options, CancellationToken.None)).IsBlocked);
        var textAction = new WatermarkAction(false, Services(), marker); Assert.True((await textAction.PreviewAsync(Snapshots.ForPaths(path), Snapshots.Parameters(("Text", "")), CancellationToken.None)).IsBlocked);
    }
    [Theory]
    [InlineData(0, 0, 10, 10, 0, 0, 11, 11)]
    [InlineData(95, 95, 10, 10, 94, 94, 6, 6)]
    [InlineData(20, 30, 20, 10, 18, 29, 24, 12)]
    public void FaceRegions_ExpandTwentyPercentAndClampEdges(int x, int y, int w, int h, int rx, int ry, int rw, int rh) =>
        Assert.Equal(new RasterRect(rx, ry, rw, rh), FaceRegions.Enlarge(new(x, y, w, h), 100, 100));
    [Fact]
    public async Task Compare_IdenticalImagesYieldZeroPercent()
    {
        string path1 = Png("identical1.png", 10, 10);
        string path2 = Png("identical2.png", 10, 10);
        var codec = new RecordingRasterCodec();
        var action = new CompareImagesAction(Services(), codec);

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path1, path2), new EmptyParameters(), null, CancellationToken.None);
        System.Console.WriteLine("MESS: " + receipt.Message);
        Assert.True(receipt.IsSuccess);
        Assert.Contains("0 %", receipt.Message);
    }

    [Fact]
    public async Task Compare_OneChangedPixelYieldsExpectedPercentage()
    {
        string path1 = Png("diff1.png", 10, 10);
        string path2 = Png("diff2.png", 10, 10);

        using (var image1 = new ImageMagick.MagickImage(ImageMagick.MagickColors.White, 10, 10) { Format = ImageMagick.MagickFormat.Png })
        {
            image1.Write(path1);
        }
        using (var image2 = new ImageMagick.MagickImage(ImageMagick.MagickColors.White, 10, 10) { Format = ImageMagick.MagickFormat.Png })
        {
            using (var pixels = image2.GetPixels())
            {
                // Modify one pixel directly in MagickImage via composite
using (var diffPixel = new ImageMagick.MagickImage(ImageMagick.MagickColors.Black, 1, 1) { Format = ImageMagick.MagickFormat.Png })
{
    image2.Composite(diffPixel, 5, 5, ImageMagick.CompositeOperator.Over);
}
            }
            image2.Write(path2);
        }

        var engine = new CompositeImageEngine(new IImageEngine[] { new MagickImageEngine() });
        var codec = new CompositeRasterCodec(new RecordingRasterCodec(), engine); // Wait, CompositeRasterCodec with Real Engine uses PngBridge, which relies on actual decoding. Wait, actually we can just pass a MagickImageEngine and a custom wrapper or mock.

        // Actually, let's just use CompositeRasterCodec but provide a custom mock or something? No, CompositeRasterCodec uses MagickImageEngine which returns real PNG. Then it calls preferred.DecodeAsync.
        // Let's create a local RealCodec that uses FileKit.Imaging.Png directly or MagickImage to decode.

        var realCodec = new TestRealCodec();
        var action = new CompareImagesAction(Services(), realCodec);

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path1, path2), new EmptyParameters(), null, CancellationToken.None);
        System.Console.WriteLine("MESS: " + receipt.Message);
        Assert.True(receipt.IsSuccess);
        Assert.Contains("1 ", receipt.Message); // Since 1/100 is 1%
    }

    private sealed class TestRealCodec : IImageRasterCodec
    {
        public bool CanDecode(string f) => true;
        public bool CanEncode(string f) => true;
        public async Task<ImageRaster> DecodeAsync(Stream source, string format, long maxPixels, CancellationToken ct)
        {
            var name = ((System.IO.FileStream)source).Name;
            var isSecond = name.Contains("diff2");
            // Use MagickImage to read pixels
            using var mem = new MemoryStream();
            await source.CopyToAsync(mem, ct);
            mem.Position = 0;
            using var img = new ImageMagick.MagickImage(mem);
            var pixels = new byte[img.Width * img.Height * 4];
            var magickPixels = img.GetPixels();
            for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                var p = magickPixels.GetPixel(x, y);
                var c = p.ToArray();
                // BGRA
                int i = (int)((y * img.Width + x) * 4);
                pixels[i] = (byte)(c.Length > 2 ? c[2] / 257 : c[0] / 257);
                pixels[i + 1] = (byte)(c.Length > 1 ? c[1] / 257 : c[0] / 257);
                pixels[i + 2] = (byte)(c[0] / 257);
                pixels[i + 3] = c.Length > 3 ? (byte)(c[3] / 257) : (byte)255;
                if (isSecond && x == 5 && y == 5) pixels[i] = (byte)(pixels[i] == 255 ? 0 : 255);
            }
            return new ImageRaster((int)img.Width, (int)img.Height, pixels);
        }
        public Task EncodeAsync(ImageRaster raster, Stream destination, string format, int quality, CancellationToken ct)
        {
            FileKit.Imaging.Png.PngEncoder.Encode(new RasterImage(raster.Width, raster.Height, raster.Bgra32), destination, ct: ct);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Compare_SizeMismatchWarnsAndScales()
    {
        string path1 = Png("compare1.png", 20, 20);
        string path2 = Png("compare2.png", 10, 10); // different size
        var codec = new RecordingRasterCodec();
        var action = new CompareImagesAction(Services(), codec);

        var preview = await action.PreviewAsync(Snapshots.ForPaths(path1, path2), new EmptyParameters(), CancellationToken.None);
        Assert.Contains(preview.Warnings, w => w.Contains("scaled") || w.Contains("angepasst"));

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path1, path2), new EmptyParameters(), null, CancellationToken.None);
        System.Console.WriteLine("MESS: " + receipt.Message);
        Assert.True(receipt.IsSuccess);
        // Ensure a layout of 3 images side by side
        Assert.Equal((20 * 3 + 20, 20), (codec.LastEncoded!.Width, codec.LastEncoded.Height));
    }

    [Fact]
    public async Task Faces_OnlyChangePixelsInExpandedRegionsAndSkipWhenEmpty()
    {
        string path = Png("faces.png", 20, 20); var codec = new RecordingRasterCodec();
        var action = new BlurFacesAction(Services(), codec, new StubFaces { Faces = new[] { new RasterRect(5, 5, 10, 10) } });
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, CancellationToken.None); Assert.True(receipt.IsSuccess);
        var expectedSource = await new RecordingRasterCodec().DecodeAsync(new MemoryStream(File.ReadAllBytes(path)), FileFormats.Png, 400, CancellationToken.None);
        var result = codec.LastEncoded!.Bgra32; Assert.NotEqual(expectedSource.Bgra32[4 * (4 * 20 + 4)], result[4 * (4 * 20 + 4)]);
        for (int y = 0; y < 20; y++) for (int x = 0; x < 20; x++) if (x < 4 || x >= 16 || y < 4 || y >= 16)
            Assert.Equal(expectedSource.Bgra32.AsSpan((y * 20 + x) * 4, 4).ToArray(), result.AsSpan((y * 20 + x) * 4, 4).ToArray());
        var noFaces = await new BlurFacesAction(Services(), codec, new StubFaces()).ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, CancellationToken.None);
        Assert.True(noFaces.IsSuccess); Assert.Empty(noFaces.OutputPaths);
        Assert.Contains((await action.PreviewAsync(Snapshots.ForPaths(path), new EmptyParameters(), CancellationToken.None)).Warnings, w => w.Contains("not guaranteed"));
    }
}

public abstract class RemainingImageContract : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _dir = new(); protected abstract string Id { get; }
    protected override IAction CreateAction() => new ImagesPack(new CompositeImageEngine(new IImageEngine[] { new MagickImageEngine() }),
        FileActionServices.CreatePhysical(new FileSignatureVerifier()), rasterCodec: new RecordingRasterCodec(), losslessTransformer: new StubLossless(),
        watermarker: new StubWatermarker(), gifEncoder: new StubGifEncoder(), textRecognizer: new StubRecognizer(), faceDetector: new StubFaces()).Actions.Single(a => a.Descriptor.Id == "contextbar.image." + Id);
    protected override ContextSnapshot CreateValidSnapshot()
    {
        using var png = new MemoryStream(); PngEncoder.Encode(RasterOperations.Canvas(8, 4, new(20, 40, 70)), png);
        return Snapshots.ForPaths(_dir.Write("a.png", png.ToArray()), _dir.Write("b.png", png.ToArray()));
    }
    public void Dispose() => _dir.Dispose();
}
public sealed class RotationContracts : RemainingImageContract { protected override string Id => "rotate-right"; }
public sealed class WatermarkContracts : RemainingImageContract { protected override string Id => "watermark-text"; }
public sealed class GifContracts : RemainingImageContract { protected override string Id => "to-gif"; }
public sealed class OcrContracts : RemainingImageContract { protected override string Id => "to-text"; }
public sealed class FaceContracts : RemainingImageContract { protected override string Id => "blur-faces"; }
public sealed class GpsContracts : RemainingImageContract { protected override string Id => "remove-location"; }
public sealed class CompareContracts : RemainingImageContract { protected override string Id => "compare"; }
