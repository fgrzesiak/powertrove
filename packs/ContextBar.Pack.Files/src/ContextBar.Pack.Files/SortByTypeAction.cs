namespace ContextBar.Pack.Files;

public sealed class SortByTypeAction(FileActionServices services) : CombineFilesAction<bool>(services)
{
    public override ActionDescriptor Descriptor => new("contextbar.files.sort-by-type", SR.GetString("SortByType"), SR.GetString("SortByTypeDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["sort", "type", "copy", "organize"], true, true,
        Selection: new SelectionRequirements(new HashSet<string>(), MinItems: 2, IdealMinDistinctFamilies: 2));
    protected override bool RequiresEntireSelection => true;
    protected override FileInputFilter Input => FileTools.Any();
    protected override string? ValidateSelection(IReadOnlyList<SelectedFile> files) => Mixed(files) ? null : SR.GetString("MixedTypesRequired");
    protected override bool ReadOptions(ActionParameterValues values) => true;
    protected override string DescribeOperation(int count, bool options) => SR.Format("SortByTypeOperation", count);
    private static bool Mixed(IEnumerable<SelectedFile> files) => files.Select(SelectionFamilies.Of).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() == 2;
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, bool options)
    {
        if (!Mixed(inputs)) throw new FileProcessingException(SR.GetString("MixedTypesRequired"));
        return new(FolderName(Path.GetDirectoryName(inputs[0].Path!)!), "", true);
    }
    private string FolderName(string directory)
    {
        directory = Services.OutputDirectoryOverride.Value ?? directory;
        string name = "Sorted", candidate = name;
        for (int n = 2; n < 10002; n++)
        {
            string path = Path.Combine(directory, candidate);
            if (!Services.FileSystem.FileExists(path) && !Services.FileSystem.DirectoryExists(path)) return candidate;
            candidate = $"{name} ({n})";
        }
        throw new FileProcessingException(SR.GetString("MixedTypesRequired"));
    }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot c, ActionParameters p, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(c, p, ct).ConfigureAwait(false);
        return c.Content is FileSelection f && Mixed(f.Files.Where(Input.Accepts)) ? preview : preview with { IsBlocked = true, PreviewText = SR.GetString("MixedTypesRequired") };
    }
    protected override Task ProcessAsync(IReadOnlyList<FileWorkItem> items, bool options, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Mixed(items.Select(i => i.File))) throw new FileProcessingException(SR.GetString("MixedTypesRequired"));
        return output.WriteDirectoryAsync(items[0].Directory, FolderName(items[0].Directory), async (directory, token) =>
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                if (FileTools.IsLink(item.Path)) throw new FileProcessingException(SR.GetString("LinksUnsupported"));
                string family = SelectionFamilies.Of(item.File);
                string key = family switch { "image" => "Images", "pdf" => "PDFs", "document" => "Documents", "spreadsheet" => "Spreadsheets", "video" => "Videos", "audio" => "Audio", "archive" => "Archives", "text" or "subtitle" => "Text", _ => "Other" };
                string folder = Path.Combine(directory, key);
                if (!Services.FileSystem.DirectoryExists(folder)) Services.FileSystem.CreateDirectory(folder);
                string originalName = Path.GetFileName(item.Path);
                string stem = Path.GetFileNameWithoutExtension(originalName), ext = Path.GetExtension(originalName), name = originalName;
                for (int n = 2; !names.Add(Path.Combine(folder, name)); n++) name = $"{stem} ({n}){ext}";
                string target = Path.Combine(folder, name);
                await using var read = Services.FileSystem.OpenRead(item.Path);
                await using var write = Services.FileSystem.CreateFile(target);
                await read.CopyToAsync(write, token).ConfigureAwait(false);
                progress?.Report((double)(item.Index + 1) / items.Count);
            }
        }, ct);
    }
}
