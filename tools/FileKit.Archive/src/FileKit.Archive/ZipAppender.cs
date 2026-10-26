using System.IO.Compression;

namespace FileKit.Archive;

/// <summary>Copies safe entries into a fresh archive; never opens the source in update mode.</summary>
public static class ZipAppender
{
    public static async Task AddAsync(Stream sourceArchive, Stream output, IReadOnlyList<string> sources,
        ZipExtractionLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= ZipExtractionLimits.Default;
        if (!sourceArchive.CanSeek || sourceArchive.Length - sourceArchive.Position > limits.MaxTotalUncompressedBytes) throw new ArchiveLimitExceededException("The archive exceeds supported limits.");
        using var input = new ZipArchive(sourceArchive, ZipArchiveMode.Read, leaveOpen: true);
        if (input.Entries.Count > limits.MaxEntries) throw new ArchiveLimitExceededException("Too many archive entries.");
        var occupied = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        string Normalize(string name) => ArchivePathRules.NormalizeEntryName(name, limits).Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/');
        void Reserve(string name, bool directory)
        {
            if (name.Length == 0 || occupied.TryGetValue(name, out bool existing) && (!existing || !directory)) throw new UnsafeArchiveEntryException("Conflicting archive entries.");
            occupied[name] = directory;
            int slash = name.LastIndexOf('/');
            while (slash > 0)
            {
                string parent = name[..slash];
                if (occupied.TryGetValue(parent, out bool isDirectory) && !isDirectory) throw new UnsafeArchiveEntryException("Conflicting archive entries.");
                occupied[parent] = true; slash = parent.LastIndexOf('/');
            }
        }
        var old = new List<(ZipArchiveEntry Entry, string Name, bool Directory)>(); long total = 0;
        foreach (var entry in input.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (entry.IsEncrypted || type != 0 && type != 0x8000 && type != 0x4000) throw new UnsafeArchiveEntryException("Unsupported archive entry type.");
            bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            string name = Normalize(entry.FullName); Reserve(name, directory);
            if (entry.Length > limits.MaxEntryUncompressedBytes || (total = checked(total + entry.Length)) > limits.MaxTotalUncompressedBytes ||
                entry.Length > 1024 * 1024 && entry.Length / (double)Math.Max(1, entry.CompressedLength) > limits.MaxCompressionRatio)
                throw new ArchiveLimitExceededException("The archive exceeds extraction limits.");
            old.Add((entry, name, directory));
        }
        // Reserve complete top-level trees before adding any new entries, so a renamed
        // folder keeps all children under the same new prefix.
        var added = ZipBuilder.BuildPlan(sources, new ZipCreationOptions { MaxEntries = limits.MaxEntries, MaxDepth = limits.MaxPathDepth, MaxTotalBytes = limits.MaxTotalUncompressedBytes }, out int skipped, cancellationToken);
        if (output is FileStream outputFile)
            added.RemoveAll(p => string.Equals(Path.GetFullPath(p.SourcePath), Path.GetFullPath(outputFile.Name), StringComparison.OrdinalIgnoreCase));
        if (skipped != 0) throw new UnsafeArchiveEntryException("Linked files are not supported.");
        if (old.Count + added.Count > limits.MaxEntries) throw new ArchiveLimitExceededException("Too many archive entries.");
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string UniqueRoot(string name, bool directory)
        {
            string stem = directory ? name : Path.GetFileNameWithoutExtension(name), ext = directory ? "" : Path.GetExtension(name);
            string candidate = name;
            for (int n = 2; occupied.ContainsKey(candidate); n++) candidate = $"{stem} ({n}){ext}";
            occupied[candidate] = directory; return candidate;
        }
        var fresh = new List<(ZipBuilder.PlanItem Item, string Name)>();
        foreach (var item in added)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Normalize(item.EntryName); int slash = name.IndexOf('/'); string root = slash < 0 ? name : name[..slash];
            if (!roots.TryGetValue(root, out var renamed)) roots[root] = renamed = UniqueRoot(root, slash >= 0 || item.IsDirectory);
            name = renamed + (slash < 0 ? "" : name[slash..]);
            if (slash >= 0) Reserve(name, item.IsDirectory);
            if (item.Length > limits.MaxEntryUncompressedBytes || (total = checked(total + item.Length)) > limits.MaxTotalUncompressedBytes) throw new ArchiveLimitExceededException("The selection exceeds archive limits.");
            fresh.Add((item, name));
        }
        long written = 0;
        using var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        async Task Copy(Stream source, Stream destination, long declared, long compressed)
        {
            long count = 0; var buffer = new byte[81920]; int n;
            while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                count += n; written += n;
                if (count > declared || count > limits.MaxEntryUncompressedBytes || written > limits.MaxTotalUncompressedBytes ||
                    compressed >= 0 && count > 1024 * 1024 && count / (double)Math.Max(1, compressed) > limits.MaxCompressionRatio)
                    throw new ArchiveLimitExceededException("An archive entry exceeds its allowed size.");
                await destination.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            }
            if (count != declared) throw new InvalidArchiveException("An input changed while it was being archived.");
        }
        foreach (var (entry, name, directory) in old)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dest = target.CreateEntry(name + (directory ? "/" : ""), CompressionLevel.Optimal); dest.LastWriteTime = entry.LastWriteTime;
            if (directory) continue;
            await using var read = entry.Open(); await using var write = dest.Open();
            await Copy(read, write, entry.Length, entry.CompressedLength).ConfigureAwait(false);
        }
        foreach (var (item, name) in fresh)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetAttributes(item.SourcePath).HasFlag(FileAttributes.ReparsePoint)) throw new UnsafeArchiveEntryException("Linked files are not supported.");
            var dest = target.CreateEntry(name + (item.IsDirectory ? "/" : ""), CompressionLevel.Optimal);
            if (item.IsDirectory) continue;
            await using var read = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            await using var write = dest.Open(); await Copy(read, write, item.Length, -1).ConfigureAwait(false);
        }
    }
}
