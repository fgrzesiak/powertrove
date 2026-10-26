using System.Formats.Tar;
using System.IO.Compression;

namespace FileKit.Archive;

public static class TarBuilder
{
    public static async Task<ZipCreationResult> CreateAsync(Stream output, IReadOnlyList<string> sourcePaths,
        ZipCreationOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= ZipCreationOptions.Default;
        if (options.FollowLinks) throw new UnsafeArchiveEntryException("TAR creation never follows links.");
        var plan = ZipBuilder.BuildPlan(sourcePaths, options, out int skipped, cancellationToken);
        long bytes = plan.Sum(p => p.Length);
        if (bytes > options.MaxTotalBytes) throw new ArchiveLimitExceededException("Selection exceeds the archive size limit.");
        int files = 0, directories = 0, done = 0; long read = 0;
        using var gzip = new GZipStream(output, options.CompressionLevel, leaveOpen: true);
        await using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var item in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArchivePathRules.NormalizeEntryName(item.EntryName, ZipExtractionLimits.Default);
                if (File.GetAttributes(item.SourcePath).HasFlag(FileAttributes.ReparsePoint)) throw new UnsafeArchiveEntryException("A source changed into a link.");
                var entry = new PaxTarEntry(item.IsDirectory ? TarEntryType.Directory : TarEntryType.RegularFile, item.EntryName) { ModificationTime = item.LastWrite };
                if (item.IsDirectory) { await writer.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false); directories++; }
                else
                {
                    await using var source = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (source.Length != item.Length) throw new InvalidArchiveException("A source file changed size.");
                    entry.DataStream = source;
                    await writer.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                    if (source.Length != item.Length || source.Position != item.Length) throw new InvalidArchiveException("A source file changed during archiving.");
                    read += item.Length; files++;
                }
                progress?.Report((double)++done / Math.Max(1, plan.Count));
            }
        }
        return new(files, directories, read, skipped);
    }
}
