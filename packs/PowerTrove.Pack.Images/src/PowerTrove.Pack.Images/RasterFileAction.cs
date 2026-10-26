using FileKit.Imaging;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;

namespace PowerTrove.Pack.Images;

public enum RasterOperation { Crop, Pad, Grayscale, OptimizePng, Icon, ConvertOther }
public sealed record RasterOptions(string Mode = "", string Anchor = "center", double? Ratio = null, int Border = 0,
    BgraColor Color = default, bool Transparent = false, int Colors = 256, bool Dither = true);

/// <summary>Per-file raster transformations share the SDK's transactional output lifecycle.</summary>
public sealed class RasterFileAction : PerFileAction<RasterOptions>
{
    private readonly RasterOperation _operation;
    private readonly IImageRasterCodec? _codec;
    private readonly CompositeImageEngine _engine;
    public RasterFileAction(RasterOperation operation, FileActionServices services, IImageRasterCodec? codec, CompositeImageEngine engine) : base(services)
    {
        _operation = operation; _codec = codec; _engine = engine;
        Descriptor = operation switch
        {
            RasterOperation.Crop => RasterSupport.Descriptor("crop-aspect", "Crop", new[] {
                RasterSupport.Choice("Ratio", "1:1", "1:1", "4:3", "3:2", "16:9", "9:16", "4:5", "custom"),
                ActionParameterDefinition.Text("CustomRatio", SR.GetString("Raster_CustomRatio"), "1:1").VisibleWhen("Ratio", "custom"),
                RasterSupport.Choice("Anchor", "center", "center", "top", "bottom", "left", "right") }),
            RasterOperation.Pad => RasterSupport.Descriptor("pad", "Pad", new[] {
                RasterSupport.Choice("Mode", "square", "square", "border"),
                ActionParameterDefinition.Integer("BorderPx", SR.GetString("Raster_BorderPx"), 20, 1, 2000).VisibleWhen("Mode", "border"),
                ActionParameterDefinition.Color("Color", SR.GetString("Raster_Color"), "#FFFFFF"),
                ActionParameterDefinition.Boolean("Transparent", SR.GetString("Raster_Transparent"), false) }),
            RasterOperation.Grayscale => RasterSupport.Descriptor("grayscale", "Grayscale"),
            RasterOperation.OptimizePng => RasterSupport.Descriptor("optimize-png", "OptimizePng", new[] {
                ActionParameterDefinition.Integer("Colors", SR.GetString("Raster_Colors"), 256, 16, 256),
                ActionParameterDefinition.Boolean("Dither", SR.GetString("Raster_Dither"), true) }),
            RasterOperation.Icon => RasterSupport.Descriptor("to-ico", "Icon", new[] { RasterSupport.Choice("Sizes", "favicon", "favicon", "windows") }),
            _ => RasterSupport.Descriptor("convert-other", "ConvertOther", new[] { RasterSupport.Choice("Format", "tiff", "tiff", "bmp", "gif", "ico") }, weight: -20)
        };
    }
    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => _operation == RasterOperation.OptimizePng ? new(SR.GetString("Image_Files"), new[] { ".png" }, new[] { FileFormats.Png }) : RasterSupport.Filter;
    protected override string? UnavailableReason => _codec is null ? SR.GetString("Raster_NoCodec") : null;
    protected override bool ReportSizeChange => _operation == RasterOperation.OptimizePng;
    protected override RasterOptions ReadOptions(ActionParameterValues v) => _operation switch
    {
        RasterOperation.Crop => new(Ratio: RasterSupport.Ratio(v.GetChoice("Ratio") == "custom" ? v.GetText("CustomRatio") : v.GetChoice("Ratio")), Anchor: v.GetChoice("Anchor")),
        RasterOperation.Pad => new(v.GetChoice("Mode"), Border: v.GetInt32("BorderPx"), Color: RasterSupport.Color(v.GetColor("Color")), Transparent: v.GetBoolean("Transparent")),
        RasterOperation.OptimizePng => new(Colors: v.GetInt32("Colors"), Dither: v.GetBoolean("Dither")),
        RasterOperation.Icon => new(v.GetChoice("Sizes")),
        RasterOperation.ConvertOther => new(v.GetChoice("Format")), _ => new()
    };
    protected override string? ValidateOptions(RasterOptions o) => _operation == RasterOperation.Crop && o.Ratio is null ? SR.GetString("Raster_InvalidRatio") : null;
    protected override string DescribeOperation(int n, RasterOptions o) => string.Format(SR.GetString("Raster_ProcessCount"), n);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, RasterOptions o)
    {
        yield return SR.GetString("Raster_MetadataRemoved");
        if (inputs.Any(f => RasterSupport.Format(f) is FileFormats.Gif or FileFormats.WebP) || _operation == RasterOperation.ConvertOther && o.Mode == "gif")
            yield return SR.GetString("ImageExportAction_Warn_Animation");
        if (_operation == RasterOperation.Pad && o.Transparent && inputs.Any(f => RasterSupport.Format(f) is not (FileFormats.Png or FileFormats.WebP)))
            yield return SR.GetString("Raster_TransparencyIgnored");
    }
    private string Suffix => _operation switch { RasterOperation.Crop => " (cropped)", RasterOperation.Pad => " (padded)", RasterOperation.Grayscale => " (grayscale)", RasterOperation.OptimizePng => " (optimized)", _ => "" };
    private string Target(SelectedFile f, RasterOptions o) => _operation switch { RasterOperation.Icon => FileFormats.Ico, RasterOperation.ConvertOther => o.Mode, _ => RasterSupport.Format(f) };
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile f, RasterOptions o)
    {
        string target = Target(f, o);
        if (_operation == RasterOperation.ConvertOther && target != FileFormats.Ico)
        {
            if (_engine.Select(new ImageTransformRequest(RasterSupport.Format(f), target, 90, null, null, false, true)) is null)
                throw new FileProcessingException(SR.GetString("Raster_NoCodec"));
        }
        else if (target != FileFormats.Ico && !_codec!.CanEncode(target)) throw new FileProcessingException(SR.GetString("Raster_NoCodec"));
        yield return new(Path.GetFileNameWithoutExtension(f.Name) + Suffix, ImageExportAction.ExtensionFor(target));
    }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false);
        if (preview.IsBlocked) return preview;
        var o = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
        try
        {
            foreach (var f in ((FileSelection)context.Content).Files.Where(Input.Accepts))
            {
                var info = await RasterSupport.ProbeAsync(Services, f, ct, _operation == RasterOperation.ConvertOther && o.Mode != "ico" ? ImageLimits.Default.MaxPixels : RasterImage.MaxPixels).ConfigureAwait(false);
                if (_operation == RasterOperation.Pad) { var (w, h) = PadSize(info.DisplaySize.Width, info.DisplaySize.Height, o); RasterImage.ByteLength(w, h); }
            }
            return preview;
        }
        catch (Exception ex) when (ex is FileProcessingException or RasterException or IOException or UnauthorizedAccessException)
        {
            return preview with { IsBlocked = true, RequiresConfirmation = false, PreviewText = ex is FileProcessingException ? ex.Message : ex is RasterException ? SR.GetString("Raster_TooManyPixels") : RasterMessages.DecodeFailed };
        }
    }
    protected override async Task ProcessAsync(FileWorkItem item, RasterOptions o, FileOutputWriter output, CancellationToken ct)
    {
        string target = Target(item.File, o);
        if (_operation == RasterOperation.ConvertOther && target != FileFormats.Ico)
        {
            await RasterSupport.ProbeAsync(Services, item.File, ct, ImageLimits.Default.MaxPixels).ConfigureAwait(false);
            await output.WriteFileAsync(item.Directory, item.BaseName, ImageExportAction.ExtensionFor(target), async (stream, token) =>
            {
                await using var source = Services.FileSystem.OpenRead(item.Path);
                await _engine.TransformAsync(source, stream, new ImageTransformRequest(item.DetectedFormat ?? RasterSupport.Format(item.File), target, 90, null, null, false, true), token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            return;
        }
        var decoded = await RasterSupport.DecodeAsync(Services, _codec!, item, ct).ConfigureAwait(false);
        if (target != FileFormats.Ico && !_codec!.CanEncode(target)) throw new FileProcessingException(SR.GetString("Raster_NoCodec"));
        var pixels = RasterSupport.Pixels(decoded);
        await Task.Run(async () =>
        {
            if (_operation == RasterOperation.Crop) pixels = RasterOperations.Crop(pixels, AspectCrop.Calculate(pixels.Width, pixels.Height, o.Ratio!.Value, o.Anchor), ct);
            if (_operation == RasterOperation.Pad)
            {
                var (w, h) = PadSize(pixels.Width, pixels.Height, o);
                bool transparent = o.Transparent && (target is FileFormats.Png or FileFormats.WebP);
                var color = transparent ? o.Color with { A = 0 } : o.Transparent ? new BgraColor(255, 255, 255) : o.Color;
                pixels = RasterOperations.Pad(pixels, w, h, (w - pixels.Width) / 2, (h - pixels.Height) / 2, color, ct);
            }
            if (_operation == RasterOperation.Grayscale) pixels = RasterOperations.Grayscale(pixels, ct);
            if (_operation == RasterOperation.OptimizePng)
            {
                using var png = new MemoryStream();
                PngEncoder.Encode(PaletteQuantizer.Quantize(pixels, o.Colors, o.Dither, ct), png, ct);
                if (png.Length >= Services.FileSystem.GetFileLength(item.Path)) throw new FileSkippedException(SR.GetString("Raster_AlreadyOptimal"));
                await output.WriteFileAsync(item.Directory, item.BaseName + Suffix, ".png", async (stream, token) =>
                { png.Position = 0; await png.CopyToAsync(stream, token).ConfigureAwait(false); }, ct).ConfigureAwait(false);
                return;
            }
            await output.WriteFileAsync(item.Directory, item.BaseName + Suffix, ImageExportAction.ExtensionFor(target), async (stream, token) =>
            {
                if (target == FileFormats.Ico) WriteIcon(pixels, stream, _operation == RasterOperation.ConvertOther ? new[] { 256 } : o.Mode == "windows" ? new[] { 16, 24, 32, 48, 64, 128, 256 } : new[] { 16, 32, 48 }, token);
                else await _codec!.EncodeAsync(RasterSupport.Map(pixels, decoded), stream, target, 90, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
    private static (int W, int H) PadSize(int w, int h, RasterOptions o) => o.Mode == "square" ? (Math.Max(w, h), Math.Max(w, h)) : (checked(w + 2 * o.Border), checked(h + 2 * o.Border));
    private static void WriteIcon(RasterImage source, Stream stream, IReadOnlyList<int> sizes, CancellationToken ct)
    {
        // Resize first so an extremely wide image does not allocate a giant square just to make a tiny icon.
        var images = new List<RasterImage>();
        foreach (int size in sizes)
        {
            double scale = (double)size / Math.Max(source.Width, source.Height);
            int w = Math.Max(1, (int)Math.Round(source.Width * scale)), h = Math.Max(1, (int)Math.Round(source.Height * scale));
            var raster = RasterOperations.ResizeBilinear(source, w, h, ct);
            raster = RasterOperations.Pad(raster, size, size, (size - w) / 2, (size - h) / 2, new BgraColor(0, 0, 0, 0), ct);
            images.Add(raster);
        }
        IcoWriter.WriteImages(stream, images, ct);
    }
}
