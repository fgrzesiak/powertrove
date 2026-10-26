using FileKit.Imaging.Raster;

namespace PowerTrove.Pack.Images;

public enum OrthogonalTransform { Identity, Rotate90, Rotate180, Rotate270, FlipHorizontal, FlipVertical, Transpose, Transverse }
public sealed record LosslessImageRequest(OrthogonalTransform Transform, int ExpectedOrientation);
public interface IImageLosslessTransformer
{
    Task TransformAsync(Stream source, Stream destination, LosslessImageRequest request, CancellationToken ct);
}

public sealed record ImageWatermarkRequest(string SourceFormat, string? Text, string Position, int Opacity,
    int FontSizePercent, string Color, int WidthPercent, string? WatermarkFormat = null);
public interface IImageWatermarker
{
    Task ApplyAsync(Stream source, Stream? watermark, Stream destination, ImageWatermarkRequest request, CancellationToken ct);
    Task ValidateAsync(Stream watermark, string format, CancellationToken ct);
}

public interface IFaceDetector
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<RasterRect>> DetectAsync(ImageRaster raster, CancellationToken ct);
}

public sealed record GifEncodingOptions(int Width, int Height, int DelayCentiseconds, bool Loop);
public interface IAnimatedGifEncoder
{
    Task EncodeAsync(IAsyncEnumerable<ImageRaster> frames, Stream destination, GifEncodingOptions options, CancellationToken ct);
}

public static class ImageTransforms
{
    public static OrthogonalTransform FromExif(int orientation) => orientation switch {
        1 => OrthogonalTransform.Identity, 2 => OrthogonalTransform.FlipHorizontal, 3 => OrthogonalTransform.Rotate180,
        4 => OrthogonalTransform.FlipVertical, 5 => OrthogonalTransform.Transpose, 6 => OrthogonalTransform.Rotate90,
        7 => OrthogonalTransform.Transverse, 8 => OrthogonalTransform.Rotate270, _ => throw new ArgumentOutOfRangeException(nameof(orientation)) };
    public static (int X, int Y) Map(int x, int y, int width, int height, OrthogonalTransform t) => t switch {
        OrthogonalTransform.Identity => (x, y), OrthogonalTransform.Rotate90 => (height - 1 - y, x),
        OrthogonalTransform.Rotate180 => (width - 1 - x, height - 1 - y), OrthogonalTransform.Rotate270 => (y, width - 1 - x),
        OrthogonalTransform.FlipHorizontal => (width - 1 - x, y), OrthogonalTransform.FlipVertical => (x, height - 1 - y),
        OrthogonalTransform.Transpose => (y, x), OrthogonalTransform.Transverse => (height - 1 - y, width - 1 - x),
        _ => throw new ArgumentOutOfRangeException(nameof(t)) };
    public static bool SwapsAxes(OrthogonalTransform t) => t is OrthogonalTransform.Rotate90 or OrthogonalTransform.Rotate270 or OrthogonalTransform.Transpose or OrthogonalTransform.Transverse;
    public static OrthogonalTransform Compose(OrthogonalTransform first, OrthogonalTransform second)
    {
        foreach (var candidate in Enum.GetValues<OrthogonalTransform>())
            if (new[] { (0, 0), (1, 0), (0, 1) }.All(p =>
            { var a = Map(p.Item1, p.Item2, 2, 2, first); return Map(a.X, a.Y, 2, 2, second) == Map(p.Item1, p.Item2, 2, 2, candidate); })) return candidate;
        throw new ArgumentOutOfRangeException(nameof(first));
    }
    public static RasterImage Apply(RasterImage image, OrthogonalTransform transform, CancellationToken ct) => transform switch {
        OrthogonalTransform.Identity => image, OrthogonalTransform.Rotate90 => RasterOperations.Rotate90(image, ct),
        OrthogonalTransform.Rotate180 => RasterOperations.Rotate180(image, ct), OrthogonalTransform.Rotate270 => RasterOperations.Rotate270(image, ct),
        OrthogonalTransform.FlipHorizontal => RasterOperations.FlipHorizontal(image, ct), OrthogonalTransform.FlipVertical => RasterOperations.FlipVertical(image, ct),
        OrthogonalTransform.Transpose => RasterOperations.FlipHorizontal(RasterOperations.Rotate90(image, ct), ct),
        OrthogonalTransform.Transverse => RasterOperations.FlipVertical(RasterOperations.Rotate90(image, ct), ct),
        _ => throw new ArgumentOutOfRangeException(nameof(transform)) };
}

/// <summary>Safe, localized adapter messages, including infrastructure failures.</summary>
public static class ExtendedImageMessages
{
    public static string Get(string key) => SR.GetString("Extra_" + key);
}
