using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerTrove.Abstractions.Manifest;

namespace PowerTrove.ReleaseTools;

/// <summary>Local publication preparation only. Retains signed releases and never uploads files.</summary>
public static class PackGitHubAssetsCommand
{
    private sealed record Release(ActionManifest Manifest, long Bytes, string Source);

    public static async Task ExecuteAsync(string packRoot, string outputDirectory, string? keyFile,
        string? previousPackRoot = null, CancellationToken token = default)
    {
        string destination = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Use a new GitHub asset directory.");
        RejectLinks(destination);
        using var signer = PackBundleBuilder.LoadSigner(keyFile);
        var releases = new Dictionary<string, Release>(StringComparer.OrdinalIgnoreCase);
        if (previousPackRoot is not null) await ReadAsync(previousPackRoot, releases, signer, token);
        await ReadAsync(packRoot, releases, signer, token);
        if (releases.Count is < 1 or > 998 || releases.Values.GroupBy(r => r.Manifest.Id).Count() > 64 ||
            releases.Values.GroupBy(r => r.Manifest.Id).Any(g => g.Count() > 16))
            throw new InvalidDataException("Pack release retention limits exceeded.");
        var ownership = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var release in releases.Values)
            foreach (var action in release.Manifest.Actions)
            {
                if (ownership.TryGetValue(action.Id, out var owner) && owner != release.Manifest.Id)
                    throw new InvalidDataException("Action IDs cannot move between packs.");
                ownership[action.Id] = release.Manifest.Id;
            }
        var ordered = releases.Values.OrderBy(r => r.Manifest.Id, StringComparer.Ordinal)
            .ThenBy(r => Version.Parse(r.Manifest.Version)).ToArray();
        byte[] catalog = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "2",
            packs = ordered.Select(r => new { manifest = JsonSerializer.Deserialize<JsonElement>(ActionPackManifest.ToJson(r.Manifest)), downloadBytes = r.Bytes }).ToArray()
        });
        if (catalog.Length > ActionPackManifest.MaximumJsonBytes) throw new InvalidDataException("Catalog exceeds the reader limit.");
        string staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var release in ordered)
            {
                token.ThrowIfCancellationRequested();
                await using var input = File.OpenRead(release.Source);
                await using var output = new FileStream(Path.Combine(staging, AssetName(release.Manifest)), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, token);
                if (output.Length != release.Bytes) throw new InvalidDataException("Bundle size changed during preparation.");
            }
            await File.WriteAllBytesAsync(Path.Combine(staging, "catalog.json"), catalog, token);
            await File.WriteAllBytesAsync(Path.Combine(staging, "catalog.json.sig"),
                signer.SignData(catalog, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence), token);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static async Task ReadAsync(string root, Dictionary<string, Release> releases, ECDsa signer, CancellationToken token)
    {
        root = Path.GetFullPath(root); RejectLinks(root);
        byte[] catalog = await ReadBoundedAsync(Path.Combine(root, "catalog.json"), ActionPackManifest.MaximumJsonBytes, token);
        byte[] signature = await ReadBoundedAsync(Path.Combine(root, "catalog.json.sig"), 128, token);
        if (!signer.VerifyData(catalog, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new InvalidDataException("Input catalog signature is invalid.");
        using var document = JsonDocument.Parse(catalog, new JsonDocumentOptions { MaxDepth = 40 });
        var schema = document.RootElement.GetProperty("schemaVersion").GetString();
        var packs = document.RootElement.GetProperty("packs");
        if (schema is not ("1" or "2") || packs.GetArrayLength() > 998) throw new InvalidDataException("Unsupported input catalog.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in packs.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var manifest = ActionPackManifest.Read(item.GetProperty("manifest").GetRawText());
            string asset = AssetName(manifest);
            string identity = manifest.Id + "@" + manifest.Version;
            long bytes = item.GetProperty("downloadBytes").GetInt64();
            if (!seen.Add(schema == "1" ? manifest.Id : identity) || manifest.SchemaVersion != "2.0" || manifest.Payload is null ||
                manifest.ProtocolVersion != "1" || manifest.Actions.Any(a => a.InputKinds.Count != 1 || a.InputKinds[0] is not ("files" or "table")) ||
                bytes is <= 0 or > 1024L * 1024 * 1024 + 1024 * 1024 || bytes > manifest.Payload.SizeBytes + 1024 * 1024)
                throw new InvalidDataException("Only signed worker-v1 file or table bundles can be published.");
            string path = Path.Combine(root, asset);
            if (!File.Exists(path)) path = Path.Combine(root, manifest.Id, manifest.Version + ".cbpack");
            RejectLinks(path);
            if (new FileInfo(path).Length != bytes) throw new InvalidDataException("Bundle is missing or its length differs from the signed catalog.");
            await VerifyBundleAsync(path, manifest, signer, token);
            var release = new Release(manifest, bytes, path);
            if (releases.TryGetValue(identity, out var existing))
            {
                if (ActionPackManifest.ToJson(existing.Manifest) != ActionPackManifest.ToJson(manifest))
                    throw new InvalidDataException("An existing pack version cannot be replaced. Increment the pack version.");
                // ECDSA signatures are randomized. Rebuilding identical payloads can produce
                // a different ZIP; keep the exact previously published verified bytes.
            }
            else releases.Add(identity, release);
        }
    }

    private static string AssetName(ActionManifest manifest)
    {
        if (!Regex.IsMatch(manifest.Id, @"\A[a-z0-9]+(?:[.-][a-z0-9]+)*\z") ||
            !Regex.IsMatch(manifest.Version, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\z") || !Version.TryParse(manifest.Version, out _))
            throw new InvalidDataException("Unsafe pack identity.");
        return manifest.Id + "-" + manifest.Version + ".cbpack";
    }
    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked publication paths are not supported.");
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        RejectLinks(path);
        await using var input = File.OpenRead(path);
        if (input.Length is <= 0 || input.Length > maximum) throw new InvalidDataException("Invalid catalog file length.");
        byte[] bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token);
        if (await input.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("Catalog changed while reading.");
        return bytes;
    }
    private static async Task VerifyBundleAsync(string path, ActionManifest expected, ECDsa key, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
            if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("Duplicate bundle entry.");
        if (entries.Count != expected.Payload!.Files.Count + 2 || !entries.TryGetValue("manifest.json", out var manifestEntry) ||
            !entries.TryGetValue("manifest.sig", out var signatureEntry)) throw new InvalidDataException("Unexpected bundle inventory.");
        byte[] manifest = await ReadEntryAsync(manifestEntry, ActionPackManifest.MaximumJsonBytes, token);
        byte[] signature = await ReadEntryAsync(signatureEntry, 128, token);
        if (!key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence) ||
            ActionPackManifest.ToJson(ActionPackManifest.Read(new UTF8Encoding(false, true).GetString(manifest))) != ActionPackManifest.ToJson(expected))
            throw new InvalidDataException("Bundle manifest differs from the signed catalog.");
        foreach (var file in expected.Payload.Files)
        {
            if (file.SizeBytes is not { } size || !entries.TryGetValue(file.Path, out var entry) || entry.Length != size)
                throw new InvalidDataException("Bundle payload length differs from inventory.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = entry.Open(); byte[] buffer = new byte[81920]; long total = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer, token); if (read == 0) break;
                total = checked(total + read);
                if (total > size) throw new InvalidDataException("Expanded payload exceeds inventory.");
                hash.AppendData(buffer, 0, read);
            }
            if (total != size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(file.Sha256)))
                throw new InvalidDataException("Bundle payload hash differs from inventory.");
        }
    }
    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, int maximum, CancellationToken token)
    {
        if (entry.Length is <= 0 || entry.Length > maximum) throw new InvalidDataException("Invalid bundle metadata length.");
        byte[] bytes = new byte[(int)entry.Length]; await using var input = entry.Open();
        await input.ReadExactlyAsync(bytes, token);
        if (await input.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("Expanded metadata exceeds limit.");
        return bytes;
    }
}
