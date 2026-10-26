using System.IO.Compression;

namespace FileKit.Archive;

public sealed record ZipCreationOptions
{
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Optimal;
    public int MaxEntries { get; init; } = 100_000;
    public long MaxTotalBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    public int MaxDepth { get; init; } = 64;
    /// <summary>Symbolic links and junctions are skipped unless this is true (cycle and scope protection).</summary>
    public bool FollowLinks { get; init; }

    public static ZipCreationOptions Default { get; } = new();
}

public sealed record ZipCreationResult(int FilesAdded, int DirectoriesAdded, long BytesRead, int LinksSkipped);

/// <summary>
/// Creates a ZIP archive from files and folders. Top-level items keep their names;
/// folder contents keep their relative paths. Never reads outside the given sources
/// (links are skipped by default) and enforces entry, size and depth limits.
/// </summary>
public static class ZipBuilder
{
    private static readonly DateTimeOffset MinZipTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MaxZipTime = new(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);

    public static async Task<ZipCreationResult> CreateAsync(
        Stream output,
        IReadOnlyList<string> sourcePaths,
        ZipCreationOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
            throw new ArgumentException("At least one source is required.", nameof(sourcePaths));
        options ??= ZipCreationOptions.Default;

        var plan = BuildPlan(sourcePaths, options, out int linksSkipped, cancellationToken);
        long totalBytes = plan.Where(p => !p.IsDirectory).Sum(p => p.Length);
        if (totalBytes > options.MaxTotalBytes)
            throw new ArchiveLimitExceededException("The selection exceeds the maximum archive size.");

        int files = 0, directories = 0;
        long read = 0;
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.IsDirectory)
                {
                    var dirEntry = zip.CreateEntry(item.EntryName + "/");
                    dirEntry.LastWriteTime = Clamp(item.LastWrite);
                    directories++;
                    continue;
                }

                var entry = zip.CreateEntry(item.EntryName, options.CompressionLevel);
                entry.LastWriteTime = Clamp(item.LastWrite);
                await using (var source = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
                await using (var destination = entry.Open())
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                        read += n;
                    }
                }

                files++;
                if (totalBytes > 0) progress?.Report(Math.Min(1.0, (double)read / totalBytes));
            }
        }

        return new ZipCreationResult(files, directories, read, linksSkipped);
    }

    internal sealed record PlanItem(string SourcePath, string EntryName, bool IsDirectory, long Length, DateTimeOffset LastWrite);

    internal static List<PlanItem> BuildPlan(IReadOnlyList<string> sources, ZipCreationOptions options, out int linksSkipped, CancellationToken cancellationToken)
    {
        var plan = new List<PlanItem>();
        var usedTopLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        linksSkipped = 0;

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string full = Path.GetFullPath(source);
            string topName = UniqueName(Path.GetFileName(Path.TrimEndingDirectorySeparator(full)), usedTopLevel);

            if (Directory.Exists(full))
            {
                var info = new DirectoryInfo(full);
                if (IsLink(info) && !options.FollowLinks)
                {
                    linksSkipped++;
                    continue;
                }
                plan.Add(new PlanItem(full, topName, true, 0, info.LastWriteTimeUtc));
                AddDirectory(info, topName, 1, options, plan, ref linksSkipped, cancellationToken);
            }
            else if (File.Exists(full))
            {
                var info = new FileInfo(full);
                if (IsLink(info) && !options.FollowLinks)
                {
                    linksSkipped++;
                    continue;
                }
                plan.Add(new PlanItem(full, topName, false, info.Length, info.LastWriteTimeUtc));
            }
            else
            {
                throw new FileNotFoundException("A source item no longer exists.");
            }

            if (plan.Count > options.MaxEntries)
                throw new ArchiveLimitExceededException($"The selection contains more than {options.MaxEntries} items.");
        }

        return plan;
    }

    private static void AddDirectory(DirectoryInfo directory, string entryPrefix, int depth, ZipCreationOptions options, List<PlanItem> plan, ref int linksSkipped, CancellationToken cancellationToken)
    {
        if (depth > options.MaxDepth)
            throw new ArchiveLimitExceededException("A folder is nested too deeply.");

        foreach (var child in directory.EnumerateFileSystemInfos().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsLink(child) && !options.FollowLinks)
            {
                linksSkipped++;
                continue;
            }

            string entryName = entryPrefix + "/" + child.Name;
            if (child is DirectoryInfo sub)
            {
                plan.Add(new PlanItem(sub.FullName, entryName, true, 0, sub.LastWriteTimeUtc));
                AddDirectory(sub, entryName, depth + 1, options, plan, ref linksSkipped, cancellationToken);
            }
            else if (child is FileInfo file)
            {
                plan.Add(new PlanItem(file.FullName, entryName, false, file.Length, file.LastWriteTimeUtc));
            }

            if (plan.Count > options.MaxEntries)
                throw new ArchiveLimitExceededException($"The selection contains more than {options.MaxEntries} items.");
        }
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;

    private static string UniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem} ({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static DateTimeOffset Clamp(DateTimeOffset value) =>
        value < MinZipTime ? MinZipTime : value > MaxZipTime ? MaxZipTime : value;
}
