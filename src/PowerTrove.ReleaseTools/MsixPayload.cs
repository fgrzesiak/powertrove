using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PowerTrove.ReleaseTools;

/// <summary>Binds the MSIX to the exact Core ZIP without republishing or changing its files.</summary>
public static class MsixPayload
{
    public sealed record PayloadFile(string Path, long Bytes, string Sha256);
    public sealed record InputEvidence(string SourceZipSha256, string SourceRevision, string HostVersion,
        string ReleaseVersion, IReadOnlyList<PayloadFile> Files);
    private const long MaximumBytes = 4L * 1024 * 1024 * 1024;

    public static async Task<InputEvidence> ExtractAsync(string zip, string destination, string expectedSha256,
        string revision, string version, string releaseVersion, CancellationToken token = default)
    {
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit) || revision.Length != 40 || !revision.All(Uri.IsHexDigit))
            throw new ArgumentException("An exact input hash and source revision are required.");
        destination = Path.GetFullPath(destination);
        RejectLinks(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Use a fresh MSIX payload directory.");
        await using var source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token)).ToLowerInvariant();
        if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Core ZIP hash mismatch.");
        source.Position = 0;
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var entries = ValidateEntries(archive);
        Directory.CreateDirectory(destination);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            string target = Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            byte[] buffer = new byte[81920]; long count = 0; int read;
            while ((read = await input.ReadAsync(buffer, token)) != 0)
            {
                count += read;
                if (count > entry.Length) throw new InvalidDataException("Core ZIP entry exceeds its declared length.");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (count != entry.Length) throw new InvalidDataException("Core ZIP entry is incomplete.");
        }
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(destination, "manifest.json"), token));
        var root = manifest.RootElement;
        if (root.GetProperty("GitRevision").GetString() != revision || root.GetProperty("Version").GetString() != version ||
            root.GetProperty("ReleaseVersion").GetString() != releaseVersion || root.GetProperty("Target").GetString() != "win-x64")
            throw new InvalidDataException("Core ZIP identity differs from the requested MSIX source.");
        return new(hash, revision, version, releaseVersion, await InventoryAsync(destination, token));
    }

    public static async Task<IReadOnlyList<PayloadFile>> VerifyAsync(string msix, string payload, InputEvidence input, CancellationToken token = default)
    {
        var files = await InventoryAsync(payload, token);
        var current = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        if (input.Files.Count == 0 || input.Files.Any(f => !current.TryGetValue(f.Path, out var value) || value != f))
            throw new InvalidDataException("MSIX preparation changed an original Core payload file.");
        using var archive = ZipFile.OpenRead(msix);
        var entries = ValidateEntries(archive).ToDictionary(e => e.FullName, StringComparer.Ordinal);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (!entries.Remove(file.Path, out var entry) || entry.Length != file.Bytes)
                throw new InvalidDataException("MSIX inventory differs from the prepared payload.");
            await using var stream = entry.Open();
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            if (hash != file.Sha256) throw new InvalidDataException("MSIX content hash mismatch.");
        }
        if (entries.Keys.Any(name => name is not ("AppxBlockMap.xml" or "[Content_Types].xml")))
            throw new InvalidDataException("MSIX contains unexpected additional content.");
        return files;
    }

    private static IReadOnlyList<ZipArchiveEntry> ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > 20000) throw new InvalidDataException("Invalid archive entry count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ZipArchiveEntry>(); long total = 0;
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName;
            bool directory = name.EndsWith('/');
            string path = directory ? name[..^1] : name;
            if (path.Length == 0 || name.Contains('\\') || path.Split('/').Any(p => p.Length == 0 || p is "." or ".." ||
                    p.EndsWith('.') || p.EndsWith(' ') || p.Any(char.IsControl) || IsDevice(p) || p.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0) ||
                !names.Add(path) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("Unsafe or duplicate archive path.");
            total = checked(total + entry.Length);
            if (total > MaximumBytes || directory && entry.Length != 0) throw new InvalidDataException("Archive size limit exceeded.");
            if (!directory) files.Add(entry);
        }
        return files;
    }

    private static bool IsDevice(string segment)
    {
        string stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9';
    }

    private static async Task<IReadOnlyList<PayloadFile>> InventoryAsync(string directory, CancellationToken token)
    {
        RejectLinks(directory);
        var files = new List<PayloadFile>();
        foreach (string path in SafeFiles(directory).Order(StringComparer.Ordinal))
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            files.Add(new(Path.GetRelativePath(directory, path).Replace('\\', '/'), stream.Length,
                Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant()));
        }
        return files;
    }
    private static IEnumerable<string> SafeFiles(string directory)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked payload content is not permitted.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string file in SafeFiles(path)) yield return file;
            }
            else yield return path;
        }
    }
    private static void RejectLinks(string path)
    {
        for (DirectoryInfo? current = new(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Payload paths must not traverse reparse points.");
    }
}
