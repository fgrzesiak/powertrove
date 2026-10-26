using System.Runtime.CompilerServices;
using FileKit.Imaging.Raster;

namespace ContextBar.Pack.Images;

public sealed record AnimatedGifOptions(int DelayMs, bool Loop, int MaxSize);
public static class GifTiming
{
    public static int Centiseconds(int delayMs) => delayMs is >= 20 and <= 10000 ? (delayMs + 9) / 10 : throw new ArgumentOutOfRangeException(nameof(delayMs));
}
public sealed class AnimatedGifAction(FileActionServices services, IImageRasterCodec? codec, IAnimatedGifEncoder? encoder) : CombineFilesAction<AnimatedGifOptions>(services)
{
    public override ActionDescriptor Descriptor { get; } = ExtraImageSupport.Descriptor("to-gif", "AnimatedGif", new[] {
        ActionParameterDefinition.Integer("DelayMs", ExtraImageSupport.Text("DelayMs"), 500, 20, 10000),
        ActionParameterDefinition.Boolean("Loop", ExtraImageSupport.Text("Loop"), true),
        ActionParameterDefinition.Integer("MaxSize", ExtraImageSupport.Text("MaxSize"), 800, 64, 2000) }, min: 2, max: 300);
    protected override FileInputFilter Input => RasterSupport.Filter;
    protected override int MaximumInputs => 300;
    protected override string? UnavailableReason => codec is null || encoder is null ? ExtraImageSupport.Text("GifUnavailable") : null;
    protected override AnimatedGifOptions ReadOptions(ActionParameterValues v) => new(v.GetInt32("DelayMs"), v.GetBoolean("Loop"), v.GetInt32("MaxSize"));
    protected override string DescribeOperation(int n, AnimatedGifOptions o) => ExtraImageSupport.Count("GifCount", n);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> files, AnimatedGifOptions o)
    {
        yield return ExtraImageSupport.Text("GifColors"); yield return SR.GetString("Raster_MetadataRemoved");
        foreach (var warning in ExtraImageSupport.AnimationWarning(files)) yield return warning;
    }
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> files, AnimatedGifOptions o) => new(Path.GetFileNameWithoutExtension(files[0].Name) + " (animation)", ".gif");
    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, AnimatedGifOptions o, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct)
    {
        var first = await RasterSupport.ProbeAsync(Services, items[0].File, ct).ConfigureAwait(false);
        var (width, height) = first.DisplaySize; double scale = Math.Min(1, (double)o.MaxSize / Math.Max(width, height));
        width = Math.Max(1, (int)Math.Round(width * scale)); height = Math.Max(1, (int)Math.Round(height * scale));
        var options = new GifEncodingOptions(width, height, GifTiming.Centiseconds(o.DelayMs), o.Loop);
        await output.WriteFileAsync(items[0].Directory, items[0].BaseName + " (animation)", ".gif",
            async (stream, token) => await encoder!.EncodeAsync(Frames(token), stream, options, token).ConfigureAwait(false), ct).ConfigureAwait(false);

        async IAsyncEnumerable<ImageRaster> Frames([EnumeratorCancellation] CancellationToken token)
        {
            foreach (var item in items)
            {
                var decoded = await RasterSupport.DecodeAsync(Services, codec!, item, token).ConfigureAwait(false);
                var frame = await Task.Run(() =>
                {
                    double fit = Math.Min((double)width / decoded.Width, (double)height / decoded.Height);
                    int w = Math.Max(1, (int)Math.Round(decoded.Width * fit)), h = Math.Max(1, (int)Math.Round(decoded.Height * fit));
                    var resized = RasterOperations.ResizeBilinear(RasterSupport.Pixels(decoded), w, h, token);
                    // GIF cannot represent partial alpha; an opaque canvas also makes disposal deterministic.
                    return RasterOperations.Compose(new[] { (resized, (width - w) / 2, (height - h) / 2) }, width, height, new BgraColor(255, 255, 255), token);
                }, token).ConfigureAwait(false);
                yield return RasterSupport.Map(frame); progress?.Report((double)(item.Index + 1) / items.Count);
            }
        }
    }
}
