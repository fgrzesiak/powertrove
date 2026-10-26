using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using ContextBar.Abstractions.Manifest;

namespace ContextBar.Application.Packs;

public sealed class PackBundleVerifier(PackSignatureVerifier signatures, Version hostVersion)
{
    public const long MaximumPayloadBytes = 1024L * 1024 * 1024;
    public const long MaximumBundleBytes = MaximumPayloadBytes + ActionPackManifest.MaximumJsonBytes + 1024 * 1024;

    public async Task<VerifiedPackBundle> OpenAsync(Stream stream, CancellationToken token)
    {
        if (!stream.CanSeek || stream.Length > MaximumBundleBytes) throw new InvalidDataException();
        await CheckCentralDirectoryAsync(stream, token).ConfigureAwait(false);
        var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        try
        {
            if (archive.Entries.Count is < 3 or > 10002) throw new InvalidDataException();
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                // Directory entries are omitted by the bundle writer. Reject aliases and Unix/DOS links.
                if (entry.FullName.EndsWith('/') || !entries.TryAdd(entry.FullName, entry)
                    || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                    || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            }
            if (!entries.TryGetValue("manifest.json", out var manifestEntry) || manifestEntry.FullName != "manifest.json"
                || !entries.TryGetValue("manifest.sig", out var signatureEntry) || signatureEntry.FullName != "manifest.sig") throw new InvalidDataException();
            byte[] manifestBytes = await ReadEntryAsync(manifestEntry, ActionPackManifest.MaximumJsonBytes, token).ConfigureAwait(false);
            byte[] signature = await ReadEntryAsync(signatureEntry, 128, token).ConfigureAwait(false);
            if (!signatures.Verify(manifestBytes, signature)) throw new InvalidDataException();
            var manifest = ParseManifest(manifestBytes, hostVersion);
            long total = 0;
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json", "manifest.sig" };
            foreach (var file in manifest.Payload!.Files)
            {
                if (!file.Path.StartsWith("payload/", StringComparison.Ordinal) || !entries.TryGetValue(file.Path, out var entry)
                    || entry.FullName != file.Path || file.SizeBytes is { } size && size != entry.Length) throw new InvalidDataException();
                total = checked(total + entry.Length);
                if (total > manifest.Payload.SizeBytes) throw new InvalidDataException();
                expected.Add(file.Path);
            }
            if (total != manifest.Payload.SizeBytes || entries.Count != expected.Count || entries.Keys.Any(p => !expected.Contains(p))) throw new InvalidDataException();
            return new VerifiedPackBundle(archive, manifest, manifestBytes, signature);
        }
        catch { archive.Dispose(); throw; }
    }

    private static async Task CheckCentralDirectoryAsync(Stream stream, CancellationToken token)
    {
        // Bound metadata BEFORE ZipArchive creates entry/name objects. Bundles are below 4 GiB
        // and 10,002 entries, so ZIP64 and multi-volume containers are unnecessary and rejected.
        if (stream.Length < 22) throw new InvalidDataException();
        byte[] tail = new byte[(int)Math.Min(stream.Length, 65557)];
        stream.Position = stream.Length - tail.Length;
        await stream.ReadExactlyAsync(tail, token).ConfigureAwait(false);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) == 0x06054b50
                && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20, 2)) == tail.Length) { end = i; break; }
        if (end < 0) throw new InvalidDataException();
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10, 2));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12, 4));
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16, 4));
        if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 4, 4)) != 0 || entries is < 3 or > 10002
            || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 8, 2)) != entries || length > 16 * 1024 * 1024
            || (long)offset + length != stream.Length - tail.Length + end) throw new InvalidDataException();
        stream.Position = offset;
        byte[] header = new byte[46]; long consumed = 0;
        for (int i = 0; i < entries; i++)
        {
            await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            int name = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
            int extra = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2));
            int comment = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2));
            consumed += 46L + name + extra + comment;
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50 || name is 0 or > 4096 || extra > 1024 || comment > 1024
                || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34, 2)) != 0 || consumed > length) throw new InvalidDataException();
            stream.Seek(name + extra + comment, SeekOrigin.Current);
        }
        if (consumed != length) throw new InvalidDataException();
        stream.Position = 0;
    }

    internal static ActionManifest ParseManifest(byte[] bytes, Version hostVersion, bool requireCompatible = true)
    {
        var manifest = ActionPackManifest.Read(new UTF8Encoding(false, true).GetString(bytes));
        if (manifest.SchemaVersion != "2.0" || manifest.Payload is not { } payload
            || payload.SizeBytes > MaximumPayloadBytes || !payload.EntryAssembly.StartsWith("payload/", StringComparison.Ordinal)
            || payload.Files.Any(f => !f.Path.StartsWith("payload/", StringComparison.Ordinal))
            || !Version.TryParse(manifest.MinHostVersion, out var minimum)
            || requireCompatible && (minimum > hostVersion || manifest.ProtocolVersion != "1"
                || manifest.Actions.Any(a => a.InputKinds.Count != 1 || a.InputKinds[0] != "files"))) throw new InvalidDataException();
        PackInstaller.ValidateId(manifest.Id); PackInstaller.ValidateVersion(manifest.Version);
        foreach (var action in manifest.Actions)
        {
            if (action.Parameters is not { } parameters) continue;
            if (parameters.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != parameters.Count) throw new InvalidDataException();
            foreach (var parameter in parameters)
            {
                if (parameter.Type == "choice" && (parameter.Choices is not { Count: > 0 and <= 1000 } choices
                    || choices.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 4096)
                    || choices.Distinct(StringComparer.OrdinalIgnoreCase).Count() != choices.Count
                    || parameter.Default is null || !choices.Contains(parameter.Default, StringComparer.OrdinalIgnoreCase))) throw new InvalidDataException();
                if (parameter.Type == "number" && parameter.Default is not null
                    && !int.TryParse(parameter.Default, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)) throw new InvalidDataException();
                if (parameter.Type == "color" && (parameter.Default is null || !System.Text.RegularExpressions.Regex.IsMatch(parameter.Default, @"\A#?[0-9a-fA-F]{6}\z"))) throw new InvalidDataException();
            }
        }
        return manifest;
    }
    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, int maximum, CancellationToken token)
    {
        if (entry.Length > maximum) throw new InvalidDataException();
        await using var input = entry.Open();
        byte[] bytes = new byte[checked((int)entry.Length)];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await input.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new InvalidDataException();
        return bytes;
    }
}

/// <summary>Verified metadata is immutable; payloads are verified while extracting, before activation.</summary>
public sealed class VerifiedPackBundle : IDisposable
{
    private readonly ZipArchive _archive;
    internal byte[] ManifestBytes { get; }
    internal byte[] Signature { get; }
    public ActionManifest Manifest { get; }
    public string ManifestSha256 => Convert.ToHexString(SHA256.HashData(ManifestBytes)).ToLowerInvariant();
    internal VerifiedPackBundle(ZipArchive archive, ActionManifest manifest, byte[] bytes, byte[] signature)
    { _archive = archive; Manifest = manifest; ManifestBytes = bytes; Signature = signature; }
    public async Task ExtractAsync(IPackInstallFileSystem files, string staging, CancellationToken token)
    {
        foreach (var file in Manifest.Payload!.Files)
        {
            var entry = _archive.GetEntry(file.Path) ?? throw new InvalidDataException();
            string path = Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar));
            files.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = files.CreateNew(path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920]; long copied = 0; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                copied = checked(copied + count);
                if (copied > entry.Length) throw new InvalidDataException();
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (copied != entry.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        }
        await WriteAsync(files, Path.Combine(staging, "manifest.json"), ManifestBytes, token).ConfigureAwait(false);
        await WriteAsync(files, Path.Combine(staging, "manifest.sig"), Signature, token).ConfigureAwait(false);
    }
    private static async Task WriteAsync(IPackInstallFileSystem files, string path, byte[] bytes, CancellationToken token)
    { await using var output = files.CreateNew(path); await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); }
    public void Dispose() => _archive.Dispose();
}
