using System.Text;
using FileKit.Tabular;

namespace ContextBar.Pack.Data;

public sealed class MergeTextAction : CombineFilesAction<string>
{
    public MergeTextAction(FileActionServices services) : base(services) { }
    public override ActionDescriptor Descriptor => new("contextbar.data.merge-text", SR.Get("merge-text"), SR.Get("Description"), new HashSet<ContentKind> { ContentKind.Files }, new[] { "text", "merge" }, true, true,
        Parameters: new[] { DataSupport.Choice("Separator", "blank-line", "none", "blank-line", "header") }, Selection: SelectionRequirements.Multiple("text", "spreadsheet", "other"));
    protected override FileInputFilter Input => new(SR.Get("Files"), DataSupport.TextExtensions, null);
    protected override string ReadOptions(ActionParameterValues v) => v.GetChoice("Separator");
    protected override string DescribeOperation(int n, string o) => string.Format(SR.Get("Operation"), n);
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, string o) => new(Path.GetFileNameWithoutExtension(inputs[0].Name) + " (merged)", ".txt");
    protected override Task ProcessAsync(IReadOnlyList<FileWorkItem> items, string o, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct) =>
        output.WriteFileAsync(items[0].Directory, items[0].BaseName + " (merged)", ".txt", async (stream, token) =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            long total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                await using var input = Services.FileSystem.OpenRead(items[i].Path);
                var text = await TextFileReader.ReadAsync(input, token).ConfigureAwait(false);
                total += Encoding.UTF8.GetByteCount(text.Text);
                if (total > TextFileReader.MaxBytes) throw new FileProcessingException(SR.Get("TooLarge"));
                if (i > 0 && o != "none") await writer.WriteAsync("\n\n".AsMemory(), token).ConfigureAwait(false);
                if (o == "header") await writer.WriteAsync(("===== " + items[i].File.Name + " =====\n").AsMemory(), token).ConfigureAwait(false);
                await writer.WriteAsync(text.Text.AsMemory(), token).ConfigureAwait(false);
                progress?.Report((double)(i + 1) / items.Count);
            }
        }, ct, allowEmpty: true);
}

public sealed class MergeCsvAction : CombineFilesAction<string>
{
    public MergeCsvAction(FileActionServices services) : base(services) { }
    public override ActionDescriptor Descriptor => new("contextbar.data.merge-csv", SR.Get("merge-csv"), SR.Get("Description"), new HashSet<ContentKind> { ContentKind.Files }, new[] { "csv", "merge" }, true, true,
        Parameters: new[] { DataSupport.Choice("Mode", "identical", "identical", "union") }, Selection: SelectionRequirements.Multiple("spreadsheet", "text"));
    protected override FileInputFilter Input => new(SR.Get("Files"), new[] { ".csv", ".tsv" }, null);
    protected override string ReadOptions(ActionParameterValues v) => v.GetChoice("Mode");
    protected override string DescribeOperation(int n, string o) => string.Format(SR.Get("Operation"), n);
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> files, string o) => new(Path.GetFileNameWithoutExtension(files[0].Name) + " (merged)", ".csv");
    private async Task<Table> Merge(IEnumerable<string> paths, string mode, CancellationToken ct)
    {
        var headers = new List<string>(); var rows = new List<IReadOnlyList<Cell>>(); bool first = true; long bytes = 0;
        try
        {
            foreach (string path in paths)
            {
                bytes += Services.FileSystem.GetFileLength(path);
                if (bytes > 100L * 1024 * 1024) throw new FileProcessingException(SR.Get("TooLarge"));
                await using var stream = Services.FileSystem.OpenRead(path);
                var table = await CsvReader.ReadAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var incoming = table.Headers ?? Array.Empty<string>();
                if (!first && mode == "identical" && !headers.SequenceEqual(incoming, StringComparer.OrdinalIgnoreCase)) throw new FileProcessingException(SR.Get("HeaderMismatch"));
                if (first) headers.AddRange(incoming);
                if (mode == "union") foreach (string header in incoming) if (!headers.Contains(header, StringComparer.OrdinalIgnoreCase)) headers.Add(header);
                if (headers.Count > 16384 || rows.Count + table.Rows.Count > 1_000_000 || (rows.Count + (long)table.Rows.Count) * headers.Count > 4_000_000) throw new FileProcessingException(SR.Get("TooLarge"));
                int[] map = incoming.Select(h => headers.FindIndex(x => string.Equals(x, h, StringComparison.OrdinalIgnoreCase))).ToArray();
                foreach (var row in table.Rows)
                {
                    ct.ThrowIfCancellationRequested();
                    var mapped = Enumerable.Repeat<Cell>(new FileKit.Tabular.EmptyCell(), headers.Count).ToArray();
                    for (int i = 0; i < map.Length; i++) mapped[map[i]] = row[i];
                    rows.Add(mapped);
                }
                first = false;
            }
            // Earlier rows may have fewer columns when later files extended a union.
            return new(headers, rows.Select(r => (IReadOnlyList<Cell>)r.Concat(Enumerable.Repeat<Cell>(new FileKit.Tabular.EmptyCell(), headers.Count - r.Count)).ToArray()).ToArray());
        }
        catch (TabularException ex) { throw DataSupport.TabularError(ex); }
    }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var p = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false); if (p.IsBlocked) return p;
        try
        {
            var table = await Merge(OrderInputs(((FileSelection)context.Content).Files.Where(Input.Accepts)).Select(f => f.Path!), ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters)), ct).ConfigureAwait(false);
            return p with { PreviewText = string.Format(SR.Get("Dimensions"), table.Rows.Count, table.Headers?.Count ?? 0) };
        }
        catch (Exception ex) when (ex is FileProcessingException or IOException or UnauthorizedAccessException) { return p with { IsBlocked = true, PreviewText = ex is FileProcessingException ? ex.Message : SR.Get("ReadError") }; }
    }
    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, string mode, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct)
    {
        var table = await Merge(items.Select(i => i.Path), mode, ct).ConfigureAwait(false);
        await output.WriteFileAsync(items[0].Directory, items[0].BaseName + " (merged)", ".csv", async (stream, token) =>
        { await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true); CsvWriter.Write(writer, table, cancellationToken: token); }, ct, allowEmpty: true).ConfigureAwait(false);
    }
}
