using FileKit.Imaging.Raster;

namespace ContextBar.Pack.Images;

public sealed class RotationAction : PerFileAction<bool>
{
    private readonly IImageRasterCodec? _codec;
    private readonly IImageLosslessTransformer? _jpeg;
    private readonly OrthogonalTransform _operation;
    private readonly bool _auto;
    public RotationAction(string id, OrthogonalTransform operation, bool auto, FileActionServices services, IImageRasterCodec? codec, IImageLosslessTransformer? jpeg) : base(services)
    {
        _operation = operation; _auto = auto; _codec = codec; _jpeg = jpeg;
        Descriptor = ExtraImageSupport.Descriptor(id, id);
    }
    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => RasterSupport.Filter;
    protected override string? UnavailableReason => _codec is null && _jpeg is null ? SR.GetString("Raster_NoCodec") : null;
    protected override bool ReadOptions(ActionParameterValues v) => true;
    protected override string DescribeOperation(int n, bool options) => ExtraImageSupport.Count("RotateCount", n);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> f, bool o)
    {
        if (f.Any(x => RasterSupport.Format(x) == FileFormats.Jpeg)) yield return ExtraImageSupport.Text("JpegMetadataKept");
        if (f.Any(x => RasterSupport.Format(x) != FileFormats.Jpeg)) yield return SR.GetString("Raster_MetadataRemoved");
        foreach (string warning in ExtraImageSupport.AnimationWarning(f)) yield return warning;
    }
    private string Suffix => _operation is OrthogonalTransform.FlipHorizontal or OrthogonalTransform.FlipVertical ? " (flipped)" : " (rotated)";
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile f, bool o)
    {
        string format = RasterSupport.Format(f);
        if (format == FileFormats.Jpeg ? _jpeg is null : _codec is null || !_codec.CanDecode(format) || !_codec.CanEncode(format))
            throw new FileProcessingException(ExtraImageSupport.Text("RotationUnavailable"));
        yield return new(Path.GetFileNameWithoutExtension(f.Name) + Suffix, ImageExportAction.ExtensionFor(format));
    }
    protected override async Task ProcessAsync(FileWorkItem item, bool options, FileOutputWriter output, CancellationToken ct)
    {
        var info = await RasterSupport.ProbeAsync(Services, item.File, ct).ConfigureAwait(false);
        if (_auto && info.Orientation == 1) throw new FileSkippedException(ExtraImageSupport.Text("AlreadyUpright"));
        string format = item.DetectedFormat ?? RasterSupport.Format(item.File);
        if (format == FileFormats.Jpeg)
        {
            if (_jpeg is null) throw new FileProcessingException(ExtraImageSupport.Text("RotationUnavailable"));
            var transform = ImageTransforms.Compose(ImageTransforms.FromExif(info.Orientation), _operation);
            await output.WriteFileAsync(item.Directory, item.BaseName + Suffix, item.File.Extension, async (stream, token) =>
            {
                await using var source = Services.FileSystem.OpenRead(item.Path);
                await _jpeg.TransformAsync(source, stream, new LosslessImageRequest(transform, info.Orientation), token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        else
        {
            if (_codec is null || !_codec.CanEncode(format)) throw new FileProcessingException(SR.GetString("Raster_NoCodec"));
            var decoded = await RasterSupport.DecodeAsync(Services, _codec, item, ct).ConfigureAwait(false);
            var raster = await Task.Run(() => ImageTransforms.Apply(RasterSupport.Pixels(decoded), _operation, ct), ct).ConfigureAwait(false);
            await output.WriteFileAsync(item.Directory, item.BaseName + Suffix, ImageExportAction.ExtensionFor(format),
                async (stream, token) => await _codec.EncodeAsync(RasterSupport.Map(raster, decoded), stream, format, 90, token).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
    }
}
