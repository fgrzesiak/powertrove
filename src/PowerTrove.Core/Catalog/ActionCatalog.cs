using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerTrove.Abstractions.Manifest;

namespace PowerTrove.Core.Catalog;

/// <summary>Discovery metadata only: reading this document never loads or installs pack code.</summary>
public sealed class ActionCatalog : IActionCatalog
{
    public const int MaximumJsonBytes = 4 * 1024 * 1024;
    public const int MaximumPacks = 64;
    public const int MaximumActions = 10000;
    public static IActionCatalog Empty { get; } = new ActionCatalog(Array.Empty<CatalogEntry>());
    public IReadOnlyList<CatalogEntry> Entries { get; }

    private ActionCatalog(IReadOnlyList<CatalogEntry> entries) => Entries = entries;

    // Installation state is derived from the host, never trusted from JSON.
    // Explicit platform arguments make compatibility checks testable on every OS.
    public static IActionCatalog Read(string json, IEnumerable<ActionPackInfo> installedPacks, Version hostVersion,
        string? os = null, string? architecture = null, bool requireWorkerInput = false)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(installedPacks);
        ArgumentNullException.ThrowIfNull(hostVersion);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new CatalogFormatException();
        var installed = installedPacks.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        os ??= OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos";
        architecture ??= RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 40 });
            var root = document.RootElement;
            CheckObject(root, "schemaVersion", "packs");
            string? schema = root.GetProperty("schemaVersion").GetString();
            if (schema is not ("1" or "2")) throw new CatalogFormatException();
            var packs = root.GetProperty("packs");
            if (packs.ValueKind != JsonValueKind.Array || packs.GetArrayLength() > (schema == "1" ? MaximumPacks : MaximumPacks * 16)) throw new CatalogFormatException();
            var entries = new List<CatalogEntry>();
            var packIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var actionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var releases = new List<(ActionManifest Manifest, long Bytes)>();
            var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in packs.EnumerateArray())
            {
                CheckObject(item, "manifest", "downloadBytes");
                long bytes = item.GetProperty("downloadBytes").GetInt64();
                if (bytes < 0) throw new CatalogFormatException();
                var manifest = ActionPackManifest.Read(item.GetProperty("manifest").GetRawText());
                if (!packIds.Add(manifest.Id) && schema == "1" || packIds.Count > MaximumPacks ||
                    !versions.Add(manifest.Id + "@" + manifest.Version)) throw new CatalogFormatException();
                // Catalog v1 requires the static v2 metadata. Legacy manifests lack
                // extension/selection information and cannot advertise reliable static fits.
                if (manifest.SchemaVersion != "2.0") throw new CatalogFormatException();
                if (!Version.TryParse(manifest.Version, out _)) throw new CatalogFormatException();
                releases.Add((manifest, bytes));
            }
            var ownership = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var release in releases)
                foreach (var action in release.Manifest.Actions)
                {
                    if (ownership.TryGetValue(action.Id, out var owner) && owner != release.Manifest.Id) throw new CatalogFormatException();
                    ownership[action.Id] = release.Manifest.Id;
                }
            foreach (var group in releases.GroupBy(r => r.Manifest.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() > 16) throw new CatalogFormatException();
                var selected = group.OrderByDescending(r => PackCompatibility.IsSupported(r.Manifest, hostVersion, os, architecture, requireWorkerInput))
                    .ThenByDescending(r => Version.Parse(r.Manifest.Version)).First();
                var manifest = selected.Manifest;
                long bytes = selected.Bytes;
                string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ActionPackManifest.ToJson(manifest))));
                var presentation = ManifestLocalization.ForCulture(manifest);
                var englishTitles = manifest.Actions.ToDictionary(a => a.Id, a => a.Title, StringComparer.Ordinal);
                var info = new ActionPackInfo(manifest.Id, presentation.DisplayName, manifest.Version, manifest.Publisher);
                if (!Version.TryParse(info.Version, out var version)) throw new CatalogFormatException();
                PackInstallState state;
                if (!PackCompatibility.IsSupported(manifest, hostVersion, os, architecture, requireWorkerInput)) state = PackInstallState.Incompatible;
                else if (installed.TryGetValue(info.Id, out var local))
                    state = Version.TryParse(local.Version, out var localVersion) && version > localVersion
                        ? PackInstallState.UpdateAvailable : PackInstallState.Installed;
                else
                    state = PackInstallState.NotInstalled;
                foreach (var action in presentation.Actions)
                {
                    if (!actionIds.Add(action.Id) || entries.Count == MaximumActions) throw new CatalogFormatException();
                    entries.Add(new CatalogEntry(action, info, state, bytes, fingerprint)
                    { EnglishTitle = englishTitles[action.Id] });
                }
            }
            return new ActionCatalog(entries.AsReadOnly());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        {
            throw new CatalogFormatException();
        }
    }

    private static void CheckObject(JsonElement element, params string[] properties)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new CatalogFormatException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name) || !properties.Contains(property.Name)) throw new CatalogFormatException();
        if (names.Count != properties.Length) throw new CatalogFormatException();
    }
}
