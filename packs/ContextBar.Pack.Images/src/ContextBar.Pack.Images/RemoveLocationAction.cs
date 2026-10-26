namespace ContextBar.Pack.Images;

public sealed class RemoveLocationAction(CompositeImageEngine engine, FileActionServices services) : PerFileAction<bool>(services)
{
    public override ActionDescriptor Descriptor { get; } = ExtraImageSupport.Descriptor("remove-location", "RemoveLocation");
    protected override FileInputFilter Input => RasterSupport.Filter;
    protected override string? UnavailableReason => engine.CanRemoveGpsOnly ? null : ExtraImageSupport.Text("GpsUnavailable");
    protected override bool ReadOptions(ActionParameterValues v) => true;
    protected override string DescribeOperation(int n, bool o) => ExtraImageSupport.Count("RemoveGpsCount", n);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> f, bool o)
    {
        yield return ExtraImageSupport.Text("GpsResave");
        foreach (string warning in ExtraImageSupport.AnimationWarning(f)) yield return warning;
    }
    private static ImageTransformRequest Request(string format) => new(format, format, 0, null, null, false, false, RemoveGpsOnly: true);
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile f, bool o)
    {
        string format = RasterSupport.Format(f);
        if (engine.Select(Request(format)) is null) throw new FileProcessingException(ExtraImageSupport.Text("GpsUnavailable"));
        yield return new(Path.GetFileNameWithoutExtension(f.Name) + " (location removed)", ImageExportAction.ExtensionFor(format));
    }
    protected override async Task ProcessAsync(FileWorkItem item, bool options, FileOutputWriter output, CancellationToken ct)
    {
        await RasterSupport.ProbeAsync(Services, item.File, ct, ImageLimits.Default.MaxPixels).ConfigureAwait(false);
        string format = item.DetectedFormat ?? RasterSupport.Format(item.File);
        await output.WriteFileAsync(item.Directory, item.BaseName + " (location removed)", ImageExportAction.ExtensionFor(format), async (stream, token) =>
        {
            await using var source = Services.FileSystem.OpenRead(item.Path);
            await engine.TransformAsync(source, stream, Request(format), token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
