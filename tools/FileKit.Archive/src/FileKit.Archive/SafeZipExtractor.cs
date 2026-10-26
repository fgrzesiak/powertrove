using System.IO.Compression;

namespace FileKit.Archive;

public sealed record ZipExtractionLimits
{
    public int MaxEntries { get; init; } = 20_000;
    public long MaxTotalUncompressedBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public long MaxEntryUncompressedBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    /// <summary>Maximum uncompressed/compressed ratio per entry (zip bomb guard). Small entries are exempt.</summary>
    public double MaxCompressionRatio { get; init; } = 250;
    public int MaxPathDepth { get; init; } = 32;
    public int MaxPathLength { get; init; } = 400;

    public static ZipExtractionLimits Default { get; } = new();
}

public sealed record ZipExtractionResult(int FilesExtracted, int DirectoriesCreated, long BytesWritten);

/// <summary>
/// Extracts ZIP archives into a directory without trusting any entry metadata:
/// paths are normalized and confined to the destination, links and devices are
/// rejected, and sizes, ratios and counts are enforced on the bytes actually written.
/// Existing files are never overwritten. On failure, already extracted files remain in
/// the destination; callers should extract into a fresh temporary directory.
/// </summary>
public static class SafeZipExtractor
{
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymlink = 0xA000;
    private const long RatioExemptBytes = 1024 * 1024;



    public static async Task<ZipExtractionResult> ExtractAsync(
        string archivePath,
        string destinationDirectory,
        ZipExtractionLimits? limits = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        return await ExtractAsync(stream, destinationDirectory, limits, progress, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ZipExtractionResult> ExtractAsync(
        Stream archive,
        string destinationDirectory,
        ZipExtractionLimits? limits = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);
        limits ??= ZipExtractionLimits.Default;

        string root = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The destination directory does not exist.");
        string rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidArchiveException("The file is not a valid ZIP archive.", ex);
        }

        using (zip)
        {
            IReadOnlyCollection<ZipArchiveEntry> entries;
            try
            {
                entries = zip.Entries;
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidArchiveException("The archive is damaged.", ex);
            }

            if (entries.Count > limits.MaxEntries)
                throw new ArchiveLimitExceededException($"The archive contains more than {limits.MaxEntries} entries.");

            long declaredTotal = 0;
            var plannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plan = new List<(ZipArchiveEntry Entry, string Target, bool IsDirectory)>(entries.Count);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsEncrypted)
                    throw new InvalidArchiveException("Encrypted archives are not supported.");

                int unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
                if (unixMode == UnixSymlink)
                    throw new UnsafeArchiveEntryException("The archive contains a symbolic link.");

                bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                string relative = NormalizeEntryName(entry.FullName, limits);
                if (relative.Length == 0)
                    continue;

                string target = Path.GetFullPath(Path.Combine(root, relative));
                if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                    throw new UnsafeArchiveEntryException("An entry would be extracted outside the destination.");
                if (!plannedPaths.Add(target) && !isDirectory)
                    throw new UnsafeArchiveEntryException("The archive contains duplicate entries.");

                if (!isDirectory)
                {
                    if (entry.Length > limits.MaxEntryUncompressedBytes)
                        throw new ArchiveLimitExceededException("An entry exceeds the maximum allowed size.");
                    declaredTotal += entry.Length;
                    if (declaredTotal > limits.MaxTotalUncompressedBytes)
                        throw new ArchiveLimitExceededException("The archive exceeds the maximum allowed uncompressed size.");
                }

                plan.Add((entry, target, isDirectory));
            }

            int files = 0, directories = 0;
            long written = 0;
            var buffer = new byte[81920];
            for (int i = 0; i < plan.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (entry, target, isDirectory) = plan[i];

                if (isDirectory)
                {
                    if (!Directory.Exists(target))
                    {
                        Directory.CreateDirectory(target);
                        directories++;
                    }
                    continue;
                }

                string? parent = Path.GetDirectoryName(target);
                if (parent != null && !Directory.Exists(parent))
                {
                    Directory.CreateDirectory(parent);
                    directories++;
                }

                long entryWritten = 0;
                bool created = false;
                try
                {
                    await using var source = entry.Open();
                    // CreateNew: never replace an existing file. Only files created here are cleaned up.
                    await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    created = true;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        entryWritten += read;
                        written += read;
                        // Enforce limits on actual bytes: declared sizes can lie.
                        if (entryWritten > limits.MaxEntryUncompressedBytes || entryWritten > entry.Length)
                            throw new ArchiveLimitExceededException("An entry is larger than declared or allowed.");
                        if (written > limits.MaxTotalUncompressedBytes)
                            throw new ArchiveLimitExceededException("The archive exceeds the maximum allowed uncompressed size.");
                        if (entryWritten > RatioExemptBytes && entry.CompressedLength > 0 &&
                            (double)entryWritten / entry.CompressedLength > limits.MaxCompressionRatio)
                            throw new ArchiveLimitExceededException("An entry has a suspicious compression ratio.");
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (InvalidDataException ex)
                {
                    if (created) TryDelete(target);
                    throw new InvalidArchiveException("The archive is damaged.", ex);
                }
                catch
                {
                    if (created) TryDelete(target);
                    throw;
                }

                try { File.SetLastWriteTime(target, entry.LastWriteTime.DateTime); } catch (ArgumentException) { } catch (IOException) { }
                files++;
                progress?.Report((double)(i + 1) / plan.Count);
            }

            return new ZipExtractionResult(files, directories, written);
        }
    }

    /// <summary>Converts an entry name to a safe relative path, or throws.</summary>
    internal static string NormalizeEntryName(string name, ZipExtractionLimits limits) => ArchivePathRules.NormalizeEntryName(name, limits);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
