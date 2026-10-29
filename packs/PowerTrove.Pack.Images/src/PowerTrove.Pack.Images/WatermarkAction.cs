using FileKit.Imaging;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;

namespace PowerTrove.Pack.Images;

public sealed record WatermarkOptions(string? Text, string? Image, string Position, int Opacity, int FontSizePercent, string Color, int WidthPercent,
    string Output = RasterSupport.SameFormat);

public sealed class WatermarkAction : PerFileAction<WatermarkOptions>
{
    private readonly IImageWatermarker? _watermarker;
    private readonly bool _image;
    private readonly IImageRasterCodec? _codec;
    public WatermarkAction(bool image, FileActionServices services, IImageWatermarker? watermarker, IImageRasterCodec? codec = null) : base(services)
    {
        _image = image; _watermarker = watermarker; _codec = codec;
        var parameters = new List<ActionParameterDefinition> {
            ExtraImageSupport.Choice("Position", "bottom-right", "top-left", "top", "top-right", "left", "center", "right", "bottom-left", "bottom", "bottom-right", "tiled"),
            ActionParameterDefinition.Integer("Opacity", ExtraImageSupport.Text("Opacity"), 35, 1, 100) };
        if (image) parameters.AddRange(new[] {
            ActionParameterDefinition.FilePath("Image", ExtraImageSupport.Text("Image"), ImageExportAction.SupportedInputExtensions),
            ActionParameterDefinition.Integer("WidthPercent", ExtraImageSupport.Text("WidthPercent"), 25, 5, 80) });
        else parameters.AddRange(new[] {
            ActionParameterDefinition.Text("Text", ExtraImageSupport.Text("Text"), ""),
            ActionParameterDefinition.Integer("FontSizePercent", ExtraImageSupport.Text("FontSizePercent"), 5, 2, 30),
            ActionParameterDefinition.Color("Color", ExtraImageSupport.Text("Color"), "#FFFFFF") });
        parameters.Add(RasterSupport.OutputFormatParameter());
        Descriptor = ExtraImageSupport.Descriptor(image ? "watermark-image" : "watermark-text", image ? "WatermarkImage" : "WatermarkText", parameters);
    }
    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => RasterSupport.Filter;
    protected override string? UnavailableReason => _watermarker is null ? ExtraImageSupport.Text("WatermarkUnavailable") : null;
    protected override WatermarkOptions ReadOptions(ActionParameterValues v)
    {
        var color = _image ? (R: (byte)255, G: (byte)255, B: (byte)255) : v.GetColor("Color");
        return new(_image ? null : v.GetText("Text"), _image ? v.GetFilePath("Image") : null, v.GetChoice("Position"), v.GetInt32("Opacity"),
            _image ? 5 : v.GetInt32("FontSizePercent"), $"#{color.R:X2}{color.G:X2}{color.B:X2}", _image ? v.GetInt32("WidthPercent") : 25,
            v.GetChoice("OutputFormat"));
    }
    protected override string? ValidateOptions(WatermarkOptions o) => !_image && (string.IsNullOrWhiteSpace(o.Text) || o.Text.Length > 500 || o.Text.Any(char.IsControl)) ? ExtraImageSupport.Text("WatermarkTextInvalid") : null;
    protected override string DescribeOperation(int n, WatermarkOptions o) => ExtraImageSupport.Count("WatermarkCount", n);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> f, WatermarkOptions o)
    {
        yield return SR.GetString("Raster_MetadataRemoved");
        foreach (var warning in ExtraImageSupport.AnimationWarning(f)) yield return warning;
        foreach (var warning in RasterSupport.OutputFormatWarnings(f, o.Output, CanWrite)) yield return warning;
    }
    // The watermarker (ImageMagick) has no HEIC encoder. Formats it cannot write go through PNG and the raster
    // codec (Windows writes HEIC with the HEVC extension); otherwise they fall back to JPEG.
    private static bool WatermarkerWrites(string format) => format is FileFormats.Jpeg or FileFormats.Png or FileFormats.WebP or FileFormats.Avif
        or FileFormats.Tiff or FileFormats.Bmp or FileFormats.Gif;
    private bool CanWrite(string format) => WatermarkerWrites(format) || _codec?.CanEncode(format) == true;
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile f, WatermarkOptions o)
    { yield return new(Path.GetFileNameWithoutExtension(f.Name) + " (watermarked)", ImageExportAction.ExtensionFor(RasterSupport.OutputFormat(RasterSupport.Format(f), o.Output, CanWrite))); }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false); if (preview.IsBlocked) return preview;
        try
        {
            var o = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
            string? format = null;
            if (_image)
            {
                format = await VerifyWatermarkAsync(o.Image!, ct).ConfigureAwait(false);
                await using var mark = Services.FileSystem.OpenRead(o.Image!);
                await _watermarker!.ValidateAsync(mark, format, ct).ConfigureAwait(false);
            }
            if (ImagePreviewRequest.IsRequested(parameters) && _codec is not null)
            {
                var file = OrderInputs(((FileSelection)context.Content).Files.Where(Input.Accepts))[0];
                var decoded = await ImagePreviews.DecodeAsync(Services, _codec, file, ct).ConfigureAwait(false);
                var thumbnail = await Task.Run(() => ImagePreviews.Thumbnail(RasterSupport.Pixels(decoded), ct), ct).ConfigureAwait(false);
                using var input = new MemoryStream();
                await Task.Run(() => PngEncoder.Encode(thumbnail, input, ct: ct), ct).ConfigureAwait(false); input.Position = 0;
                using var rendered = new MemoryStream();
                await using var mark = _image ? Services.FileSystem.OpenRead(o.Image!) : null;
                await _watermarker!.ApplyAsync(input, mark, rendered, new ImageWatermarkRequest(FileFormats.Png, o.Text, o.Position,
                    o.Opacity, o.FontSizePercent, o.Color, o.WidthPercent, format), ct).ConfigureAwait(false);
                rendered.Position = 0;
                var output = await _codec.DecodeAsync(rendered, FileFormats.Png, 512 * 512, ct).ConfigureAwait(false);
                preview = preview with { ImagePreview = await Task.Run(() => ImagePreviews.Frame(RasterSupport.Pixels(output), ct, decoded.Width, decoded.Height), ct).ConfigureAwait(false) };
            }
            return preview;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        { return preview with { IsBlocked = true, RequiresConfirmation = false, PreviewText = ExtraImageSupport.Text(_image ? "WatermarkImageInvalid" : "WatermarkFailed") }; }
    }
    private async Task<string> VerifyWatermarkAsync(string path, CancellationToken ct)
    {
        if (!Services.FileSystem.FileExists(path) || Services.FileSystem.GetFileLength(path) > ImageLimits.Default.MaxFileBytes) throw new FileProcessingException(ExtraImageSupport.Text("WatermarkImageInvalid"));
        var check = await Services.TypeVerifier.VerifyAsync(path, RasterSupport.Filter, ct).ConfigureAwait(false);
        if (!check.IsAccepted) throw new FileProcessingException(ExtraImageSupport.Text("WatermarkImageInvalid"));
        await using var source = Services.FileSystem.OpenRead(path);
        try
        {
            var info = await ImageProbe.ProbeAsync(source, ct).ConfigureAwait(false);
            if (info.PixelCount > 60_000_000 || info.Width > 30000 || info.Height > 30000) throw new ImageProbeException("Limit");
        }
        catch (ImageProbeException ex) { throw new FileProcessingException(ExtraImageSupport.Text("WatermarkImageInvalid"), ex); }
        return check.DetectedFormat ?? ImageExportAction.FormatFromExtension(Path.GetExtension(path)) ?? throw new FileProcessingException(ExtraImageSupport.Text("WatermarkImageInvalid"));
    }
    protected override async Task ProcessAsync(FileWorkItem item, WatermarkOptions o, FileOutputWriter output, CancellationToken ct)
    {
        await RasterSupport.ProbeAsync(Services, item.File, ct).ConfigureAwait(false);
        string? markFormat = _image ? await VerifyWatermarkAsync(o.Image!, ct).ConfigureAwait(false) : null;
        string sourceFormat = item.DetectedFormat ?? RasterSupport.Format(item.File);
        string targetFormat = RasterSupport.OutputFormat(sourceFormat, o.Output, CanWrite);
        await output.WriteFileAsync(item.Directory, item.BaseName + " (watermarked)", ImageExportAction.ExtensionFor(targetFormat), async (stream, token) =>
        {
            await using var source = Services.FileSystem.OpenRead(item.Path);
            await using var watermark = _image ? Services.FileSystem.OpenRead(o.Image!) : null;
            bool direct = WatermarkerWrites(targetFormat);
            using var bridge = direct ? null : new MemoryStream();
            await _watermarker!.ApplyAsync(source, watermark, bridge ?? stream, new ImageWatermarkRequest(sourceFormat, o.Text, o.Position, o.Opacity,
                o.FontSizePercent, o.Color, o.WidthPercent, markFormat, direct ? targetFormat : FileFormats.Png), token).ConfigureAwait(false);
            if (bridge is null) return;
            bridge.Position = 0;
            var raster = await _codec!.DecodeAsync(bridge, FileFormats.Png, RasterImage.MaxPixels, token).ConfigureAwait(false);
            await _codec.EncodeAsync(raster, stream, targetFormat, 90, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
