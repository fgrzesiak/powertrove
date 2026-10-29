using FileKit.Imaging;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;
using System.Text;

namespace PowerTrove.Pack.Images;

public sealed record ImageCanvasOptions(int CanvasWidth, int CanvasHeight, int ImageWidth, int ImageHeight, int OffsetX, int OffsetY, int Angle);

/// <summary>A single-image layout action. The editor passes geometry; the pack owns decoding and output.</summary>
public sealed class ImageCanvasAction(FileActionServices services, IImageRasterCodec? codec, ISvgRenderer? svgRenderer)
    : PerFileAction<ImageCanvasOptions>(services)
{
    public override ActionDescriptor Descriptor => new("powertrove.image.edit", SR.GetString("Canvas_Title"), SR.GetString("Canvas_Description"),
        new HashSet<ContentKind> { ContentKind.Files }, ["image", "edit", "canvas", "resize", "rotate", "align", "visual"], true, true,
        Parameters: [Size("CanvasWidth"), Size("CanvasHeight"), Size("ImageWidth"), Size("ImageHeight"),
            ActionParameterDefinition.Integer("OffsetX", SR.GetString("Canvas_OffsetX"), 0, -20000, 20000),
            ActionParameterDefinition.Integer("OffsetY", SR.GetString("Canvas_OffsetY"), 0, -20000, 20000),
            ActionParameterDefinition.Integer("Angle", SR.GetString("Canvas_Angle"), 0, -180, 180)],
        Selection: SelectionRequirements.Single("image"), CanRememberParameters: false);
    protected override int MaximumInputs => 1;
    protected override bool RequiresEntireSelection => true;
    protected override FileInputFilter Input => new(SR.GetString("Image_Files"), [.. ImageExportAction.SupportedInputExtensions, ".svg"], null);
    protected override string? UnavailableReason => codec is null ? RasterMessages.DecodeFailed : null;
    protected override ImageCanvasOptions ReadOptions(ActionParameterValues v) => new(v.GetInt32("CanvasWidth"), v.GetInt32("CanvasHeight"),
        v.GetInt32("ImageWidth"), v.GetInt32("ImageHeight"), v.GetInt32("OffsetX"), v.GetInt32("OffsetY"), v.GetInt32("Angle"));
    protected override string? ValidateOptions(ImageCanvasOptions o) =>
        (long)o.CanvasWidth * o.CanvasHeight > ImageCanvasLayout.MaximumPixels || (long)o.ImageWidth * o.ImageHeight > ImageCanvasLayout.MaximumPixels
            ? SR.GetString("Canvas_Invalid") : null;
    protected override string DescribeOperation(int count, ImageCanvasOptions o) => SR.GetString("Canvas_Description");
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile f, ImageCanvasOptions o) => [new(Path.GetFileNameWithoutExtension(f.Name) + " (edited)", ".png")];

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false);
        if (preview.IsBlocked) return preview;
        try
        {
            var o = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
            var source = await LoadAsync(((FileSelection)context.Content).Files[0], null, ct).ConfigureAwait(false);
            var layout = Resolve(o, source.Width, source.Height);
            double scale = Math.Min(1, 512.0 / Math.Max(source.Raster.Width, source.Raster.Height));
            var thumbnail = await Task.Run(() => RasterOperations.ResizeBilinear(source.Raster,
                Math.Max(1, (int)Math.Round(source.Raster.Width * scale)), Math.Max(1, (int)Math.Round(source.Raster.Height * scale)), ct), ct).ConfigureAwait(false);
            using var png = new MemoryStream();
            await Task.Run(() => PngEncoder.Encode(thumbnail, png, ct: ct), ct).ConfigureAwait(false);
            return preview with { PreviewText = string.Format(SR.GetString("Canvas_Preview"), layout.CanvasWidth, layout.CanvasHeight),
                Warnings = layout.IsClipped ? [SR.GetString("Canvas_OutputNote"), SR.GetString("Canvas_Clipped")] : [SR.GetString("Canvas_OutputNote")],
                ImagePreview = new ImagePreviewData(source.Width, source.Height, thumbnail.Width, thumbnail.Height, Convert.ToBase64String(png.ToArray())) };
        }
        catch (Exception ex) when (ex is FileProcessingException or RasterException or SvgInputException or IOException or UnauthorizedAccessException)
        { return preview with { IsBlocked = true, PreviewText = SR.GetString("Canvas_LoadFailed") }; }
    }

    protected override async Task ProcessAsync(FileWorkItem item, ImageCanvasOptions o, FileOutputWriter output, CancellationToken ct)
    {
        try
        {
            var source = await LoadAsync(item.File, o, ct).ConfigureAwait(false);
            var layout = Resolve(o, source.Width, source.Height);
            await output.WriteFileAsync(item.Directory, item.BaseName + " (edited)", ".png", async (stream, token) =>
            {
                await Task.Run(() => PngEncoder.Encode(ImageCanvasRenderer.Render(source.Raster, layout, token), stream, ct: token), token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RasterException or SvgInputException) { throw new FileProcessingException(SR.GetString("Canvas_LoadFailed"), ex); }
    }

    private async Task<Source> LoadAsync(SelectedFile file, ImageCanvasOptions? options, CancellationToken ct)
    {
        if (string.Equals(file.Extension, ".svg", StringComparison.OrdinalIgnoreCase))
        {
            if (svgRenderer is null) throw new FileProcessingException(SR.GetString("SvgUnavailable"));
            await using var input = Services.FileSystem.OpenRead(file.Path!);
            var safe = await SvgRasterSource.ReadAsync(input, 512, 512, ct).ConfigureAwait(false);
            double referenceScale = Math.Min(1, 4096 / Math.Max(safe.ViewBoxWidth, safe.ViewBoxHeight));
            int width = Math.Max(1, (int)Math.Round(safe.ViewBoxWidth * referenceScale));
            int height = Math.Max(1, (int)Math.Round(safe.ViewBoxHeight * referenceScale));
            if (options is not null)
            {
                var target = Resolve(options, width, height);
                safe = SvgRasterSource.Prepare(Encoding.UTF8.GetBytes(safe.Xml), target.ImageWidth, target.ImageHeight, ct);
            }
            using var png = new MemoryStream();
            await svgRenderer.RenderAsync(safe, png, ct).ConfigureAwait(false); png.Position = 0;
            var decoded = await codec!.DecodeAsync(png, FileFormats.Png, ImageCanvasLayout.MaximumPixels, ct).ConfigureAwait(false);
            return new(RasterSupport.Pixels(decoded), width, height);
        }
        var info = await RasterSupport.ProbeAsync(Services, file, ct).ConfigureAwait(false);
        string format = info.Format switch
        {
            ImageFormat.Png => FileFormats.Png, ImageFormat.Jpeg => FileFormats.Jpeg,
            ImageFormat.Gif => FileFormats.Gif, ImageFormat.Bmp => FileFormats.Bmp,
            ImageFormat.Tiff => FileFormats.Tiff, ImageFormat.WebP => FileFormats.WebP,
            ImageFormat.Heif => FileFormats.Heic, ImageFormat.Avif => FileFormats.Avif,
            _ => throw new FileProcessingException(RasterMessages.DecodeFailed)
        };
        if (!codec!.CanDecode(format)) throw new FileProcessingException(SR.GetString("Raster_NoCodec"));
        await using var stream = Services.FileSystem.OpenRead(file.Path!);
        var raster = RasterSupport.Pixels(await codec.DecodeAsync(stream, format, RasterImage.MaxPixels, ct).ConfigureAwait(false));
        return new(raster, raster.Width, raster.Height);
    }

    public static ImageCanvasLayout Resolve(ImageCanvasOptions o, int width, int height)
        => ImageCanvasLayout.Resolve(width, height, o.CanvasWidth, o.CanvasHeight, o.ImageWidth, o.ImageHeight, o.OffsetX, o.OffsetY, o.Angle);

    private static ActionParameterDefinition Size(string key) => ActionParameterDefinition.Integer(key, SR.GetString("Canvas_" + key), 0, 0, 10000, SR.GetString("Canvas_Auto"));
    private sealed record Source(RasterImage Raster, int Width, int Height);
}
