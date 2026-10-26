using FileKit.Imaging.Raster;

namespace ContextBar.Pack.Images;

public static class FaceRegions
{
    public static RasterRect? Enlarge(RasterRect face, int width, int height)
    {
        if (face.Width < 1 || face.Height < 1) return null;
        long extraX = (long)Math.Ceiling(face.Width * 0.1), extraY = (long)Math.Ceiling(face.Height * 0.1);
        int left = (int)Math.Clamp((long)face.X - extraX, 0, width), top = (int)Math.Clamp((long)face.Y - extraY, 0, height);
        int right = (int)Math.Clamp((long)face.X + face.Width + extraX, 0, width), bottom = (int)Math.Clamp((long)face.Y + face.Height + extraY, 0, height);
        return right > left && bottom > top ? new(left, top, right - left, bottom - top) : null;
    }
    public static int BlockSize(RasterRect face, bool strong) => Math.Clamp((int)Math.Round(face.Width * (strong ? 0.20 : 0.08)), 2, RasterImage.MaxDimension);
}
public sealed class BlurFacesAction(FileActionServices services, IImageRasterCodec? codec, IFaceDetector? detector) : PerFileAction<bool>(services)
{
    public override ActionDescriptor Descriptor { get; } = ExtraImageSupport.Descriptor("blur-faces", "BlurFaces", new[] { ExtraImageSupport.Choice("Strength", "strong", "light", "strong") });
    protected override FileInputFilter Input => RasterSupport.Filter;
    protected override string? UnavailableReason => codec is null || detector?.IsAvailable != true ? ExtraImageSupport.Text("FaceUnavailable") : null;
    protected override bool ReadOptions(ActionParameterValues v) => v.GetChoice("Strength") == "strong";
    protected override string DescribeOperation(int n, bool o) => ExtraImageSupport.Count("FaceCount", n);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> f, bool o)
    {
        yield return ExtraImageSupport.Text("FaceWarning"); yield return SR.GetString("Raster_MetadataRemoved");
        foreach (var warning in ExtraImageSupport.AnimationWarning(f)) yield return warning;
    }
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile f, bool o)
    {
        if (!codec!.CanEncode(RasterSupport.Format(f))) throw new FileProcessingException(SR.GetString("Raster_NoCodec"));
        yield return new(Path.GetFileNameWithoutExtension(f.Name) + " (faces blurred)", ImageExportAction.ExtensionFor(RasterSupport.Format(f)));
    }
    protected override async Task ProcessAsync(FileWorkItem item, bool strong, FileOutputWriter output, CancellationToken ct)
    {
        var decoded = await RasterSupport.DecodeAsync(Services, codec!, item, ct).ConfigureAwait(false);
        var faces = await detector!.DetectAsync(decoded, ct).ConfigureAwait(false);
        if (faces.Count > 1024) throw new FileProcessingException(ExtraImageSupport.Text("FaceFailed"));
        var regions = faces.Select(f => (Face: f, Region: FaceRegions.Enlarge(f, decoded.Width, decoded.Height))).Where(x => x.Region.HasValue)
            .Select(x => (Rect: x.Region!.Value, BlockSize: FaceRegions.BlockSize(x.Face, strong))).ToArray();
        if (regions.Length == 0) throw new FileSkippedException(ExtraImageSupport.Text("NoFaces"));
        var pixels = await Task.Run(() => RasterOperations.PixelateRegions(RasterSupport.Pixels(decoded), regions, ct), ct).ConfigureAwait(false);
        string format = item.DetectedFormat ?? RasterSupport.Format(item.File);
        await output.WriteFileAsync(item.Directory, item.BaseName + " (faces blurred)", ImageExportAction.ExtensionFor(format),
            async (stream, token) => await codec!.EncodeAsync(RasterSupport.Map(pixels, decoded), stream, format, 90, token).ConfigureAwait(false), ct).ConfigureAwait(false);
    }
}
