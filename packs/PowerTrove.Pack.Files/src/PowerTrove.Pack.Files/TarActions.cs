using FileKit.Archive;

namespace PowerTrove.Pack.Files;

public sealed class CompressToTarAction : CombineFilesAction<bool>
{
    public CompressToTarAction(FileActionServices services) : base(services) { }
    public override ActionDescriptor Descriptor => FileTools.Descriptor("tar", "TarCreate");
    protected override FileInputFilter Input => FileTools.Any(true);
    protected override int MinimumInputs => 1;
    protected override bool ReadOptions(ActionParameterValues v) => true;
    protected override string DescribeOperation(int n, bool o) => SR.Format("ProcessItems", n);
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, bool o) => new(inputs[0].IsDirectory ? inputs[0].Name : Path.GetFileNameWithoutExtension(inputs[0].Name), ".tar.gz");
    protected override Task ProcessAsync(IReadOnlyList<FileWorkItem> items, bool o, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct)
    {
        var plan = PlanOutput(items.Select(i => i.File).ToArray(), o);
        return output.WriteFileAsync(items[0].Directory, plan.BaseName, plan.Extension, async (stream, token) =>
        {
            try { await TarBuilder.CreateAsync(stream, items.Select(i => i.Path).ToArray(), progress: progress, cancellationToken: token).ConfigureAwait(false); }
            catch (ArchiveException ex) { throw new FileProcessingException(SR.GetString("ArchiveError"), ex); }
        }, ct);
    }
}

public sealed class ExtractTarAction : PerFileAction<bool>
{
    public ExtractTarAction(FileActionServices services) : base(services) { }
    public override ActionDescriptor Descriptor => FileTools.Descriptor("untar", "TarExtract", selection: SelectionRequirements.Any("archive"));
    protected override FileInputFilter Input => new(SR.GetString("Archives"), new[] { ".tar", ".gz", ".tgz" }, new[] { FileFormats.Tar, FileFormats.Gzip });
    protected override bool ReadOptions(ActionParameterValues v) => true;
    protected override string DescribeOperation(int n, bool o) => SR.Format("ProcessItems", n);
    private static bool IsTar(SelectedFile f) => f.Extension.Equals(".tar", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".tgz", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, bool o)
    {
        string name = Path.GetFileNameWithoutExtension(file.Name);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name);
        yield return IsTar(file) ? new(name, "", true) : new(Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
    }
    protected override async Task ProcessAsync(FileWorkItem item, bool o, FileOutputWriter output, CancellationToken ct)
    {
        var plan = PlanOutputs(item.File, o).Single();
        try
        {
            if (IsTar(item.File))
                await output.WriteDirectoryAsync(item.Directory, plan.BaseName, async (directory, token) =>
                { await SafeTarExtractor.ExtractAsync(item.Path, directory, item.DetectedFormat == FileFormats.Gzip || !item.File.Extension.Equals(".tar", StringComparison.OrdinalIgnoreCase), cancellationToken: token).ConfigureAwait(false); }, ct).ConfigureAwait(false);
            else
                await output.WriteFileAsync(item.Directory, plan.BaseName, plan.Extension, async (stream, token) =>
                { await using var source = Services.FileSystem.OpenRead(item.Path); await GZipFile.DecompressAsync(source, stream, cancellationToken: token).ConfigureAwait(false); }, ct, allowEmpty: true).ConfigureAwait(false);
        }
        catch (ArchiveException ex) { throw new FileProcessingException(SR.GetString("ArchiveError"), ex); }
    }
}
