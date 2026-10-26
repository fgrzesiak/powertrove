namespace ContextBar.Pack.Images;

public sealed record TargetSizeResult(byte[] Bytes, int Quality, double Scale, int Attempts);

public static class TargetSizeSearch
{
    /// <summary>At most seven quality attempts for each of the original size and five reductions.</summary>
    public static async Task<TargetSizeResult?> FindAsync(long targetBytes, Func<int, double, CancellationToken, Task<byte[]>> encode, CancellationToken ct)
    {
        if (targetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(targetBytes));
        int attempts = 0;
        for (int step = 0; step <= 5; step++)
        {
            double scale = Math.Pow(0.85, step); int low = 30, high = 95;
            TargetSizeResult? best = null;
            for (int trial = 0; trial < 7 && low <= high; trial++)
            {
                ct.ThrowIfCancellationRequested(); int quality = (low + high) / 2;
                byte[] bytes = await encode(quality, scale, ct).ConfigureAwait(false); attempts++;
                if (bytes.LongLength <= targetBytes && bytes.Length > 0) { best = new(bytes, quality, scale, attempts); low = quality + 1; }
                else high = quality - 1;
            }
            if (best is not null) return best with { Attempts = attempts };
        }
        return null;
    }
}

public sealed record TargetSizeOptions(int TargetKb, string Format);

public sealed class TargetSizeAction(CompositeImageEngine engine, FileActionServices services) : PerFileAction<TargetSizeOptions>(services)
{
    public override ActionDescriptor Descriptor { get; } = RasterSupport.Descriptor("target-size", "TargetSize", new[] {
        ActionParameterDefinition.Integer("TargetKb", SR.GetString("Raster_TargetKb"), 500, 10, 50000),
        RasterSupport.Choice("Format", "same", "same", "jpeg", "webp") });
    protected override FileInputFilter Input => RasterSupport.Filter;
    protected override TargetSizeOptions ReadOptions(ActionParameterValues v) => new(v.GetInt32("TargetKb"), v.GetChoice("Format"));
    protected override string DescribeOperation(int n, TargetSizeOptions o) => string.Format(SR.GetString("Raster_TargetPreview"), n, o.TargetKb);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> f, TargetSizeOptions o)
    {
        yield return SR.GetString("Raster_MetadataRemoved");
        if (f.Any(x => RasterSupport.Format(x) is FileFormats.Gif or FileFormats.WebP)) yield return SR.GetString("ImageExportAction_Warn_Animation");
        if (o.Format == "jpeg") yield return SR.GetString("ImageExportAction_Warn_Transparency");
    }
    private static string Target(SelectedFile f, TargetSizeOptions o) => o.Format == "same" ? RasterSupport.Format(f) : o.Format;
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile f, TargetSizeOptions o)
    {
        string target = Target(f, o);
        if (engine.Select(new ImageTransformRequest(RasterSupport.Format(f), target, 85, null, null, false, true)) is null)
            throw new FileProcessingException(SR.GetString("Raster_NoCodec"));
        yield return new(Path.GetFileNameWithoutExtension(f.Name) + " (compressed)", ImageExportAction.ExtensionFor(target));
    }
    protected override async Task ProcessAsync(FileWorkItem item, TargetSizeOptions o, FileOutputWriter output, CancellationToken ct)
    {
        var info = await RasterSupport.ProbeAsync(Services, item.File, ct, ImageLimits.Default.MaxPixels).ConfigureAwait(false);
        long targetBytes = o.TargetKb * 1024L;
        if (Services.FileSystem.GetFileLength(item.Path) <= targetBytes) throw new FileSkippedException(SR.GetString("Raster_AlreadySmall"));
        string target = Target(item.File, o), sourceFormat = item.DetectedFormat ?? RasterSupport.Format(item.File);
        var (w, h) = info.DisplaySize;
        var result = await TargetSizeSearch.FindAsync(targetBytes, async (quality, scale, token) =>
        {
            var request = new ImageTransformRequest(sourceFormat, target, quality, Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)), false, true);
            using var encoded = new MemoryStream(); await using var source = Services.FileSystem.OpenRead(item.Path);
            await engine.TransformAsync(source, encoded, request, token).ConfigureAwait(false); return encoded.ToArray();
        }, ct).ConfigureAwait(false);
        if (result is null) throw new FileProcessingException(SR.GetString("Raster_TargetUnreachable"));
        await output.WriteFileAsync(item.Directory, item.BaseName + " (compressed)", ImageExportAction.ExtensionFor(target),
            async (stream, token) => await stream.WriteAsync(result.Bytes, token).ConfigureAwait(false), ct).ConfigureAwait(false);
    }
    protected override string? DescribeResult(IReadOnlyList<string> outputs, ExecutionStatistics stats) =>
        outputs.Count > 0 ? string.Format(SR.GetString("Raster_TargetResult"), outputs.Count) : null;
}
