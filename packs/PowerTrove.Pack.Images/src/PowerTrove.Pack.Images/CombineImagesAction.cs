using FileKit.Imaging.Raster;
using FileKit.Imaging;

namespace PowerTrove.Pack.Images;

public sealed record CombineImageOptions(string Direction, string Align, int Gap, BgraColor Background, int Columns, int CellSize, bool Cover);

public sealed class CombineImagesAction : CombineFilesAction<CombineImageOptions>
{
    private readonly IImageRasterCodec? _codec;
    private readonly bool _grid;
    public CombineImagesAction(bool grid, FileActionServices services, IImageRasterCodec? codec) : base(services)
    {
        _grid = grid; _codec = codec;
        var parameters = new List<ActionParameterDefinition> {
            ActionParameterDefinition.Integer("Gap", SR.GetString("Raster_Gap"), 0, 0, 2000),
            ActionParameterDefinition.Color("Background", SR.GetString("Raster_Background"), "#FFFFFF") };
        if (grid) parameters.AddRange(new[] {
            ActionParameterDefinition.Integer("Columns", SR.GetString("Raster_Columns"), 0, 0, 100),
            ActionParameterDefinition.Integer("CellSize", SR.GetString("Raster_CellSize"), 600, 100, 2000),
            RasterSupport.Choice("Fit", "cover", "cover", "contain") });
        else parameters.AddRange(new[] { RasterSupport.Choice("Direction", "horizontal", "horizontal", "vertical"), RasterSupport.Choice("Align", "center", "start", "center", "end") });
        Descriptor = RasterSupport.Descriptor(grid ? "collage" : "stitch", grid ? "Collage" : "Stitch", parameters, min: 2, max: grid ? 100 : 50);
    }
    public override ActionDescriptor Descriptor { get; }
    protected override int MaximumInputs => _grid ? 100 : 50;
    protected override FileInputFilter Input => RasterSupport.Filter;
    protected override string? UnavailableReason => _codec is null ? SR.GetString("Raster_NoCodec") : null;
    protected override CombineImageOptions ReadOptions(ActionParameterValues v) => new(_grid ? "" : v.GetChoice("Direction"), _grid ? "center" : v.GetChoice("Align"), v.GetInt32("Gap"), RasterSupport.Color(v.GetColor("Background")), _grid ? v.GetInt32("Columns") : 0, _grid ? v.GetInt32("CellSize") : 600, !_grid || v.GetChoice("Fit") == "cover");
    protected override string DescribeOperation(int n, CombineImageOptions o) => string.Format(SR.GetString("Raster_CombineCount"), n);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> f, CombineImageOptions o)
    {
        yield return SR.GetString("Raster_MetadataRemoved"); yield return SR.GetString("ImageExportAction_Warn_Animation");
    }
    // The asynchronous preview resolves this conservative extension from container alpha flags.
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> f, CombineImageOptions o) => new(Path.GetFileNameWithoutExtension(f[0].Name) + " (combined)", ".png");
    private ImageLayout Layout(IReadOnlyList<ImageSize> sizes, CombineImageOptions o) => _grid ? ImageLayouts.Grid(sizes, o.Columns, o.CellSize, o.Gap, o.Cover) : ImageLayouts.Stitch(sizes, o.Direction == "horizontal", o.Gap, o.Align);
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false);
        if (preview.IsBlocked) return preview;
        try
        {
            var sizes = new List<ImageSize>(); bool alpha = false;
            var files = OrderInputs(((FileSelection)context.Content).Files.Where(Input.Accepts));
            foreach (var f in files)
            {
                var info = await RasterSupport.ProbeAsync(Services, f, ct).ConfigureAwait(false); var (w, h) = info.DisplaySize; sizes.Add(new(w, h));
                alpha |= await HasAlphaAsync(f, ct).ConfigureAwait(false);
            }
            var options = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
            var layout = Layout(sizes, options);
            string extension = alpha ? ".png" : ".jpg";
            var plan = Services.Planner.PlanFile(Path.GetDirectoryName(files[0].Path!)!, Path.GetFileNameWithoutExtension(files[0].Name) + " (combined)", extension);
            ImagePreviewData? frame = null;
            if (ImagePreviewRequest.IsRequested(parameters))
            {
                double scale = Math.Min(1, 512.0 / Math.Max(layout.Width, layout.Height));
                var layers = new List<(RasterImage Image, int X, int Y)>();
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var decoded = await ImagePreviews.DecodeAsync(Services, _codec!, files[i], ct).ConfigureAwait(false);
                    if (decoded.Width != sizes[i].Width || decoded.Height != sizes[i].Height) throw new FileProcessingException(RasterMessages.DecodeFailed);
                    var placement = layout.Images[i];
                    var pixels = await Task.Run(() =>
                    {
                        var original = RasterSupport.Pixels(decoded);
                        if (placement.Crop is RasterRect crop) original = RasterOperations.Crop(original, crop, ct);
                        return RasterOperations.ResizeBilinear(original, Math.Max(1, (int)Math.Round(placement.Width * scale)),
                            Math.Max(1, (int)Math.Round(placement.Height * scale)), ct);
                    }, ct).ConfigureAwait(false);
                    layers.Add((pixels, (int)Math.Round(placement.X * scale), (int)Math.Round(placement.Y * scale)));
                }
                frame = await Task.Run(() => ImagePreviews.Frame(RasterOperations.Compose(layers,
                    Math.Max(1, (int)Math.Round(layout.Width * scale)), Math.Max(1, (int)Math.Round(layout.Height * scale)),
                    options.Background, ct), ct, layout.Width, layout.Height), ct).ConfigureAwait(false);
            }
            return preview with { ExpectedOutputs = new[] { plan.TargetPath }, Warnings = preview.Warnings.Concat(new[] { SR.GetString("Raster_CombineExtension") }).ToArray(), ImagePreview = frame };
        }
        catch (Exception ex) when (ex is RasterException or FileProcessingException or IOException or UnauthorizedAccessException)
        { return preview with { IsBlocked = true, RequiresConfirmation = false, PreviewText = ex is FileProcessingException ? ex.Message : ex is RasterException ? SR.GetString("Raster_TooManyPixels") : RasterMessages.DecodeFailed }; }
    }
    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, CombineImageOptions o, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct)
    {
        var sizes = new List<ImageSize>();
        foreach (var item in items)
        {
            var info = await RasterSupport.ProbeAsync(Services, item.File, ct).ConfigureAwait(false); var (w, h) = info.DisplaySize; sizes.Add(new(w, h));
        }
        var layout = Layout(sizes, o);
        var layers = new List<(RasterImage Image, int X, int Y)>(); bool alpha = false;
        for (int i = 0; i < items.Count; i++)
        {
            var decoded = await RasterSupport.DecodeAsync(Services, _codec!, items[i], ct).ConfigureAwait(false);
            if (decoded.Width != sizes[i].Width || decoded.Height != sizes[i].Height) throw new FileProcessingException(RasterMessages.DecodeFailed);
            alpha |= await HasAlphaAsync(items[i].File, ct).ConfigureAwait(false);
            var placement = layout.Images[i];
            var raster = await Task.Run(() =>
            {
                var pixels = RasterSupport.Pixels(decoded);
                if (placement.Crop is RasterRect crop) pixels = RasterOperations.Crop(pixels, crop, ct);
                return RasterOperations.ResizeBilinear(pixels, placement.Width, placement.Height, ct);
            }, ct).ConfigureAwait(false);
            layers.Add((raster, placement.X, placement.Y)); progress?.Report((double)(i + 1) / (items.Count + 1));
        }
        var canvas = await Task.Run(() => RasterOperations.Compose(layers, layout.Width, layout.Height, o.Background, ct), ct).ConfigureAwait(false);
        string target = alpha ? FileFormats.Png : FileFormats.Jpeg;
        if (!_codec!.CanEncode(target)) throw new FileProcessingException(SR.GetString("Raster_NoCodec"));
        await output.WriteFileAsync(items[0].Directory, items[0].BaseName + " (combined)", ImageExportAction.ExtensionFor(target),
            async (stream, token) => await _codec.EncodeAsync(RasterSupport.Map(canvas), stream, target, 90, token).ConfigureAwait(false), ct).ConfigureAwait(false);
    }
    private async Task<bool> HasAlphaAsync(SelectedFile file, CancellationToken ct)
    {
        await using var stream = Services.FileSystem.OpenRead(file.Path!);
        try { return (await ImageProbe.ProbeMetadataFlagsAsync(stream, ct).ConfigureAwait(false)).HasAlpha; }
        catch (ImageProbeException ex) { throw new FileProcessingException(SR.GetString("Raster_MetadataFailed"), ex); }
    }
}
