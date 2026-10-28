using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerTrove.Abstractions.Manifest;

namespace PowerTrove.Application.Packs;

public sealed record InstalledPack(ActionManifest Manifest, string Directory, string ManifestSha256)
{
    public string WorkerGroupId => Manifest.Id + "@" + Manifest.Version;
}
public sealed record PackActivation(string Version, string ManifestSha256, string? PreviousVersion = null, bool Confirmed = false);

/// <summary>First-party local installation. No assembly loading, network request, elevation or association.</summary>
public sealed class PackInstaller
{
    private readonly IPackInstallFileSystem _files;
    private readonly PackSignatureVerifier _signatures;
    private readonly PackBundleVerifier _bundles;
    private readonly Version _hostVersion;
    public Version HostVersion => _hostVersion;
    private readonly string _root;
    private readonly Func<string, CancellationToken, Task> _stopWorkers;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PackInstaller(string userRoot, Version hostVersion, Func<string, CancellationToken, Task> stopWorkers,
        IPackInstallFileSystem? files = null, PackSignatureVerifier? signatures = null)
    {
        _files = files ?? new PhysicalPackInstallFileSystem(); _signatures = signatures ?? new PackSignatureVerifier();
        _hostVersion = hostVersion; _bundles = new(_signatures, hostVersion); _stopWorkers = stopWorkers;
        _root = Path.Combine(PackOutputCommitter.Canonical(userRoot), "packs"); _files.CheckSafePath(_root);
    }

    public async Task<InstalledPack> InstallAsync(string bundlePath, CancellationToken token = default, ActionManifest? expectedManifest = null)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? staging = null, newVersion = null;
        bool activated = false;
        try
        {
            await using var input = _files.OpenRead(PackOutputCommitter.Canonical(bundlePath));
            using var bundle = await _bundles.OpenAsync(input, token).ConfigureAwait(false);
            if (expectedManifest is not null && ActionPackManifest.ToJson(expectedManifest) != ActionPackManifest.ToJson(bundle.Manifest))
                throw new InvalidDataException();
            string packRoot = PackRoot(bundle.Manifest.Id);
            _files.CreateDirectory(packRoot);
            var previous = await ReadActivationAsync(packRoot, token).ConfigureAwait(false);
            if (previous is not null && Version.Parse(bundle.Manifest.Version) <= Version.Parse(previous.Version)) throw new InvalidDataException();
            string target = Path.Combine(packRoot, bundle.Manifest.Version);
            if (_files.DirectoryExists(target) || _files.FileExists(target)) throw new InvalidDataException();
            staging = Path.Combine(packRoot, ".staging-" + Guid.NewGuid().ToString("N"));
            _files.CreateDirectory(staging);
            await bundle.ExtractAsync(_files, staging, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await _stopWorkers(bundle.Manifest.Id, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _files.MoveDirectory(staging, target); staging = null; newVersion = target;
            await ActivateAsync(packRoot, new PackActivation(bundle.Manifest.Version, bundle.ManifestSha256, previous?.Version), token).ConfigureAwait(false);
            activated = true;
            return new InstalledPack(bundle.Manifest, target, bundle.ManifestSha256);
        }
        finally
        {
            try
            {
                if (staging is not null) _files.DeleteDirectory(staging);
                if (!activated && newVersion is not null) _files.DeleteDirectory(newVersion);
            }
            finally { _gate.Release(); }
        }
    }

    public async Task<IReadOnlyList<InstalledPack>> GetInstalledAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var result = new List<InstalledPack>();
            var directories = _files.Directories(_root);
            if (directories.Count > 64) throw new InvalidDataException();
            foreach (string directory in directories)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string id = Path.GetFileName(directory); ValidateId(id);
                    string packRoot = PackRoot(id);
                    var active = await ReadActivationAsync(packRoot, token).ConfigureAwait(false);
                    if (active is null) continue;
                    string versionRoot = Path.Combine(packRoot, active.Version);
                    byte[] manifest = await ReadAsync(Path.Combine(versionRoot, "manifest.json"), ActionPackManifest.MaximumJsonBytes, token).ConfigureAwait(false);
                    byte[] signature = await ReadAsync(Path.Combine(versionRoot, "manifest.sig"), 128, token).ConfigureAwait(false);
                    if (!_signatures.Verify(manifest, signature) || !Convert.ToHexString(SHA256.HashData(manifest)).Equals(active.ManifestSha256, StringComparison.OrdinalIgnoreCase)) continue;
                    var parsed = PackBundleVerifier.ParseManifest(manifest, _hostVersion);
                    if (parsed.Id == id && parsed.Version == active.Version) result.Add(new InstalledPack(parsed, versionRoot, active.ManifestSha256));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or System.Text.DecoderFallbackException)
                { /* Corrupt/untrusted local installations are never exposed as executable actions. */ }
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task ConfirmStartedAsync(string id, string version, CancellationToken token = default)
    {
        ValidateVersion(version);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string root = PackRoot(id);
            var active = await ReadActivationAsync(root, token).ConfigureAwait(false);
            if (active is null || active.Version != version) return;
            if (!active.Confirmed) await ActivateAsync(root, active with { Confirmed = true, PreviousVersion = null }, token).ConfigureAwait(false);
            foreach (string directory in _files.Directories(root))
            {
                string name = Path.GetFileName(directory);
                if (name == version) continue;
                // Only owned numeric version folders and our GUID staging folders may be removed.
                if (IsVersion(name) || name.StartsWith(".staging-", StringComparison.Ordinal) && Guid.TryParseExact(name[9..], "N", out _))
                    _files.DeleteDirectory(directory);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task UninstallAsync(string id, CancellationToken token = default)
    {
        string root = PackRoot(id);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _stopWorkers(id, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _files.DeleteDirectory(root);
        }
        finally { _gate.Release(); }
    }

    private async Task<PackActivation?> ReadActivationAsync(string root, CancellationToken token)
    {
        string path = Path.Combine(root, "active.json");
        if (!_files.FileExists(path)) return null;
        var bytes = await ReadAsync(path, 4096, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != document.RootElement.EnumerateObject().Count()) throw new InvalidDataException();
        var activation = JsonSerializer.Deserialize<PackActivation>(bytes) ?? throw new InvalidDataException();
        ValidateVersion(activation.Version);
        if (activation.PreviousVersion is not null) ValidateVersion(activation.PreviousVersion);
        if (activation.ManifestSha256 is null || activation.ManifestSha256.Length != 64 || activation.ManifestSha256.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException();
        return activation;
    }
    private async Task ActivateAsync(string root, PackActivation activation, CancellationToken token)
    {
        string temporary = Path.Combine(root, ".active-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool replaced = false;
        try
        {
            await using (var output = _files.CreateNew(temporary))
            {
                await JsonSerializer.SerializeAsync(output, activation, cancellationToken: token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                if (output is FileStream file) file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            _files.ActivateFile(temporary, Path.Combine(root, "active.json"));
            replaced = true;
        }
        finally { if (!replaced && _files.FileExists(temporary)) _files.DeleteFile(temporary); }
    }
    private async Task<byte[]> ReadAsync(string path, int maximum, CancellationToken token)
    {
        await using var input = _files.OpenRead(path);
        if (input.Length > maximum) throw new InvalidDataException();
        byte[] bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await input.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new InvalidDataException();
        return bytes;
    }
    private string PackRoot(string id) { ValidateId(id); string path = Path.Combine(_root, id); _files.CheckSafePath(path); return path; }
    internal static void ValidateId(string id)
    {
        if (id is null || id.Length > 128 || !Regex.IsMatch(id, @"\Apowertrove\.[a-z0-9]+(?:[.-][a-z0-9]+)*\z", RegexOptions.CultureInvariant)) throw new InvalidDataException();
    }
    internal static void ValidateVersion(string version) { if (!IsVersion(version)) throw new InvalidDataException(); }
    private static bool IsVersion(string? version) => version is { Length: <= 50 } && Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)
        && Version.TryParse(version, out var parsed) && parsed.ToString() == version;
}
