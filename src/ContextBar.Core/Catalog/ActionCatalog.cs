using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ContextBar.Abstractions.Manifest;

namespace ContextBar.Core.Catalog;

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
        string? os = null, string? architecture = null)
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
            if (root.GetProperty("schemaVersion").GetString() != "1") throw new CatalogFormatException();
            var packs = root.GetProperty("packs");
            if (packs.ValueKind != JsonValueKind.Array || packs.GetArrayLength() > MaximumPacks) throw new CatalogFormatException();
            var entries = new List<CatalogEntry>();
            var packIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var actionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in packs.EnumerateArray())
            {
                CheckObject(item, "manifest", "downloadBytes");
                long bytes = item.GetProperty("downloadBytes").GetInt64();
                if (bytes < 0) throw new CatalogFormatException();
                var manifest = ActionPackManifest.Read(item.GetProperty("manifest").GetRawText());
                if (!packIds.Add(manifest.Id)) throw new CatalogFormatException();
                // Catalog v1 requires the static v2 metadata. Legacy manifests lack
                // extension/selection information and cannot advertise reliable static fits.
                if (manifest.SchemaVersion != "2.0") throw new CatalogFormatException();
                var info = new ActionPackInfo(manifest.Id, manifest.DisplayName, manifest.Version, manifest.Publisher);
                if (!Version.TryParse(info.Version, out var version)) throw new CatalogFormatException();
                PackInstallState state;
                if (installed.TryGetValue(info.Id, out var local))
                    state = Version.TryParse(local.Version, out var localVersion) && version > localVersion
                        ? PackInstallState.UpdateAvailable : PackInstallState.Installed;
                else
                    state = manifest.ProtocolVersion == ActionPackManifest.ProtocolVersion &&
                        Version.Parse(manifest.MinHostVersion!) <= hostVersion &&
                        Supports(manifest.PlatformConstraints?.Os, os) &&
                        Supports(manifest.PlatformConstraints?.MinimumArchitecture, architecture)
                        ? PackInstallState.NotInstalled : PackInstallState.Incompatible;
                foreach (var action in manifest.Actions)
                {
                    if (!actionIds.Add(action.Id) || entries.Count == MaximumActions) throw new CatalogFormatException();
                    entries.Add(new CatalogEntry(action, info, state, bytes));
                }
            }
            return new ActionCatalog(entries.AsReadOnly());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        {
            throw new CatalogFormatException();
        }
    }

    private static bool Supports(IReadOnlyList<string>? allowed, string value) =>
        allowed is null || allowed.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static void CheckObject(JsonElement element, params string[] properties)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new CatalogFormatException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name) || !properties.Contains(property.Name)) throw new CatalogFormatException();
        if (names.Count != properties.Length) throw new CatalogFormatException();
    }
}
