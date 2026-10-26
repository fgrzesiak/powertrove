using System.Text;

namespace ContextBar.Pack.Files;

public sealed record FileReport(string Content, string Message, bool WriteFile = true);

public abstract class FileReportAction : CombineFilesAction<string>
{
    protected FileReportAction(FileActionServices services) : base(services) { }
    protected override int MinimumInputs => 1;
    protected override FileInputFilter Input => FileTools.Any(true);
    protected override string ReadOptions(ActionParameterValues v) => "file";
    protected override string DescribeOperation(int n, string o) => SR.Count("ProcessItems", n);
    protected abstract Task<FileReport> GenerateAsync(IReadOnlyList<FileWorkItem> items, string options, IProgress<double>? progress, CancellationToken ct);
    protected override Task ProcessAsync(IReadOnlyList<FileWorkItem> items, string options, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct) => WriteAsync(items, options, output, null, progress, ct);
    protected override Task ProcessWithStatisticsAsync(IReadOnlyList<FileWorkItem> items, string options, FileOutputWriter output, ExecutionStatistics statistics, IProgress<double>? progress, CancellationToken ct) => WriteAsync(items, options, output, statistics, progress, ct);
    private async Task WriteAsync(IReadOnlyList<FileWorkItem> items, string options, FileOutputWriter output, ExecutionStatistics? stats, IProgress<double>? progress, CancellationToken ct)
    {
        FileReport report = await Task.Run(() => GenerateAsync(items, options, progress, ct), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (report.WriteFile)
        {
            var plan = PlanOutput(items.Select(i => i.File).ToArray(), options);
            await FileTools.WriteText(output, items[0].Directory, plan.BaseName, plan.Extension, report.Content, ct, bom: true).ConfigureAwait(false);
        }
        if (stats is not null) { stats.ResultMessage = report.Message; stats.CompletedWithoutOutput = !report.WriteFile; }
    }
}

public sealed class ScanReportAction : FileReportAction
{
    private readonly bool _duplicates; private readonly IDirectoryScanner _scanner;
    public ScanReportAction(FileActionServices services, bool duplicates, IDirectoryScanner? scanner = null) : base(services) { _duplicates = duplicates; _scanner = scanner ?? new ReportScanner(); }
    public override ActionDescriptor Descriptor => FileTools.Descriptor(_duplicates ? "find-duplicates" : "folder-report", _duplicates ? "FindDuplicates" : "FolderReport", selection: _duplicates ? SelectionRequirements.Any() : SelectionRequirements.Any("folder"));
    protected override FileInputFilter Input => _duplicates ? FileTools.Any(true) : new(SR.GetString("Folders"), null, null, acceptFiles: false, acceptDirectories: true);
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, string o) => new(_duplicates ? "Duplicate files report" : "Folder report", ".csv");
    protected override async Task<FileReport> GenerateAsync(IReadOnlyList<FileWorkItem> items, string options, IProgress<double>? progress, CancellationToken ct)
    {
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        bool limited = false; int skipped = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.File.IsDirectory)
            {
                int remaining = 200000 - paths.Count; if (remaining <= 0) { limited = true; break; }
                var scan = _scanner is ICancellableDirectoryScanner cancellable ? cancellable.EnumerateFiles(item.Path, 20, remaining, false, ct) : _scanner.EnumerateFiles(item.Path, 20, remaining);
                foreach (string path in scan.Paths) paths.Add(path);
                limited |= scan.LimitReached; skipped += scan.SkippedDirectories;
            }
            else if (paths.Count < 200000 && !FileTools.IsLink(item.Path)) paths.Add(item.Path);
            else limited = true;
        }
        var files = new List<(string Path, long Size)>();
        foreach (string path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try { if (!FileTools.IsLink(path)) files.Add((path, new FileInfo(path).Length)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        var sb = new StringBuilder();
        if (!_duplicates)
        {
            long size = files.Sum(f => f.Size);
            sb.Append("Metric,Value\r\nTotal bytes,").Append(size).Append("\r\nFiles,").Append(files.Count).Append("\r\nSkipped,").Append(skipped).Append("\r\nLimit reached,").Append(limited).Append("\r\n\r\nFamily,Files,Bytes\r\n");
            foreach (var group in files.GroupBy(f => SelectionFamilies.Of(new SelectedFile(f.Path, "", Path.GetExtension(f.Path), null, f.Size, true, true, null))).OrderBy(g => g.Key))
                sb.Append(FileTools.Escape(group.Key)).Append(',').Append(group.Count()).Append(',').Append(group.Sum(f => f.Size)).Append("\r\n");
            sb.Append("\r\nPath,Bytes\r\n");
            foreach (var file in files.OrderByDescending(f => f.Size).ThenBy(f => f.Path, StringComparer.Ordinal).Take(20)) sb.Append(FileTools.Escape(file.Path)).Append(',').Append(file.Size).Append("\r\n");
            return new(sb.ToString(), SR.Format("FolderSummary" + (files.Count == 1 ? "_One" : "_Other"), files.Count, size, skipped, limited ? SR.GetString("ScanLimit") : ""));
        }
        sb.Append("Group,Size,Path\r\n").Append(FileTools.Escape(SR.GetString("ReportNote"))).Append(",,\r\n");
        int groups = 0, processed = 0; long reclaimable = 0;
        foreach (var sizeGroup in files.GroupBy(f => f.Size).Where(g => g.Count() > 1))
        {
            var partials = new Dictionary<string, List<(string Path, long Size)>>();
            foreach (var file in sizeGroup)
            {
                ct.ThrowIfCancellationRequested();
                string key = Convert.ToHexString(await FileTools.Hash(file.Path, true, ct).ConfigureAwait(false));
                if (!partials.TryGetValue(key, out var list)) partials[key] = list = new(); list.Add(file);
            }
            foreach (var candidates in partials.Values.Where(g => g.Count > 1))
            {
                var hashes = new Dictionary<string, List<(string Path, long Size)>>();
                foreach (var file in candidates)
                {
                    string hash = Convert.ToHexString(await FileTools.Hash(file.Path, false, ct).ConfigureAwait(false));
                    if (new FileInfo(file.Path).Length != file.Size) throw new FileProcessingException(SR.GetString("InputChanged"));
                    if (!hashes.TryGetValue(hash, out var list)) hashes[hash] = list = new(); list.Add(file);
                    progress?.Report((double)++processed / Math.Max(1, files.Count));
                }
                foreach (var group in hashes.Values.Where(g => g.Count > 1))
                {
                    groups++; reclaimable += group[0].Size * (group.Count - 1L);
                    foreach (var file in group.OrderBy(f => f.Path, StringComparer.Ordinal)) sb.Append(groups).Append(',').Append(file.Size).Append(',').Append(FileTools.Escape(file.Path)).Append("\r\n");
                }
            }
        }
        return new(sb.ToString(), SR.Format("DuplicateSummary" + (groups == 1 ? "_One" : "_Other"), groups, reclaimable, skipped, limited ? SR.GetString("ScanLimit") : ""));
    }
}
