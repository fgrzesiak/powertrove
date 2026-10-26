using FileKit.Imaging;

namespace PowerTrove.Pack.Images;

/// <summary>Read-only facts, never raw EXIF/XMP values or selected names.</summary>
public sealed class ImageInfoAction(FileActionServices services) : CombineFilesAction<bool>(services)
{
    public override ActionDescriptor Descriptor { get; } = RasterSupport.Descriptor("info", "Info", max: 20) with { RequiresPreview = false, ChangesFiles = false };
    protected override FileInputFilter Input => RasterSupport.Filter;
    protected override int MinimumInputs => 1;
    protected override int MaximumInputs => 20;
    protected override bool ReadOptions(ActionParameterValues v) => true;
    protected override string DescribeOperation(int n, bool o) => string.Format(SR.GetString("Raster_InfoPreview"), n);
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> f, bool o) => throw new InvalidOperationException();
    protected override IReadOnlyList<string> PlanExpectedOutputs(IReadOnlyList<SelectedFile> f, bool o) => Array.Empty<string>();
    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, bool options, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct) =>
        _ = await ReadFactsAsync(items, progress, ct).ConfigureAwait(false);
    protected override async Task ProcessWithStatisticsAsync(IReadOnlyList<FileWorkItem> items, bool options, FileOutputWriter output, ExecutionStatistics stats, IProgress<double>? progress, CancellationToken ct)
    {
        stats.ResultMessage = await ReadFactsAsync(items, progress, ct).ConfigureAwait(false);
        stats.CompletedWithoutOutput = true;
    }
    private async Task<string> ReadFactsAsync(IReadOnlyList<FileWorkItem> items, IProgress<double>? progress, CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var item in items)
        {
            var info = await RasterSupport.ProbeAsync(Services, item.File, ct, ImageLimits.Default.MaxPixels).ConfigureAwait(false);
            ImageMetadataFlags metadata;
            await using var source = Services.FileSystem.OpenRead(item.Path);
            try { metadata = await ImageProbe.ProbeMetadataFlagsAsync(source, ct).ConfigureAwait(false); }
            catch (ImageProbeException ex) { throw new FileProcessingException(SR.GetString("Raster_MetadataFailed"), ex); }
            string Dpi(double? value) => value?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) ?? SR.GetString("Raster_Unknown");
            string Flag(bool? value) => SR.GetString(value is true ? "Raster_Yes" : value is false ? "Raster_No" : "Raster_Unknown");
            lines.Add(string.Format(SR.GetString("Raster_InfoLine"), item.Index + 1, info.Format, info.Width, info.Height, info.PixelCount / 1_000_000.0,
                Dpi(metadata.DpiX), Dpi(metadata.DpiY), info.Orientation, Flag(metadata.HasLocation), Flag(metadata.HasColorProfile), metadata.FrameCount?.ToString() ?? SR.GetString("Raster_Unknown")));
            progress?.Report((double)lines.Count / items.Count);
        }
        return string.Join(Environment.NewLine, lines);
    }
}
