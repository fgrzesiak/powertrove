using FileKit.Archive;

namespace PowerTrove.Pack.Files;

public sealed class AddToZipAction(FileActionServices services) : CombineFilesAction<bool>(services)
{
    public override ActionDescriptor Descriptor => new("contextbar.files.add-to-zip", SR.GetString("AddToZip"), SR.GetString("AddToZipDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["zip", "add", "archive"], true, true,
        Selection: new SelectionRequirements(new HashSet<string>(), MinItems: 2,
            AllowOtherItems: true, FamilyLimits: new Dictionary<string, FamilyLimit>(StringComparer.OrdinalIgnoreCase) { ["archive"] = new(1, 1) }));
    protected override bool RequiresEntireSelection => true;
    protected override FileInputFilter Input => FileTools.Any(true);
    protected override string? ValidateSelection(IReadOnlyList<SelectedFile> files) => Valid(files) ? null : SR.GetString("ZipRoleSelection");
    protected override bool ReadOptions(ActionParameterValues v) => true;
    protected override IReadOnlyList<SelectedFile> OrderInputs(IEnumerable<SelectedFile> files) => NaturalFileNameComparer.Sort(files).OrderBy(f => IsZip(f) ? 0 : 1).ToArray();
    private static bool IsZip(SelectedFile f) => !f.IsDirectory && f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    private static bool Valid(IReadOnlyList<SelectedFile> files) => files.Count >= 2 && files.Count(IsZip) == 1 && files.Count(f => SelectionFamilies.Of(f) == "archive") == 1;
    protected override string DescribeOperation(int count, bool options) => SR.GetString("AddToZipDescription");
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, bool options)
    {
        if (!Valid(inputs)) throw new FileProcessingException(SR.GetString("ZipRoleSelection"));
        return new(Path.GetFileNameWithoutExtension(inputs[0].Name) + " (updated)", ".zip");
    }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot c, ActionParameters p, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(c, p, ct).ConfigureAwait(false);
        return c.Content is FileSelection f && Valid(f.Files) ? preview : preview with { IsBlocked = true, PreviewText = SR.GetString("ZipRoleSelection") };
    }
    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, bool options, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Valid(items.Select(x => x.File).ToArray())) throw new FileProcessingException(SR.GetString("ZipRoleSelection"));
        var archive = items.Single(i => IsZip(i.File));
        await output.WriteFileAsync(archive.Directory, archive.BaseName + " (updated)", ".zip", async (stream, token) =>
        {
            await using var source = Services.FileSystem.OpenRead(archive.Path);
            try { await ZipAppender.AddAsync(source, stream, items.Where(i => i != archive).Select(i => i.Path).ToArray(), cancellationToken: token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is ArchiveException or InvalidDataException) { throw new FileProcessingException(SR.GetString("UnsafeZip"), ex); }
        }, ct).ConfigureAwait(false);
    }
}
