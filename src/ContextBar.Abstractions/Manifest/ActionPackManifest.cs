using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using ContextBar.Abstractions.Files;

namespace ContextBar.Abstractions.Manifest;

/// <summary>
/// Derives the declarative action pack manifest (docs/references/action-pack-manifest.md)
/// from a compiled pack, so built-in packs and future packaged extensions are described
/// by the same document without hand-maintained JSON.
/// </summary>
public static class ActionPackManifest
{
    public const string SchemaVersion = "2.0";
    public const string ProtocolVersion = "1";
    public const int MaximumJsonBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ActionManifest Create(IActionPack pack, IReadOnlyList<string>? permissions = null,
        Version? minHostVersion = null, ManifestPayload? payload = null, IReadOnlyList<string>? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return new ActionManifest(
            SchemaVersion,
            pack.Info.Id,
            pack.Info.Version,
            pack.Info.DisplayName,
            pack.Info.Publisher,
            ProtocolVersion,
            pack.Actions.Select(ToManifestAction).ToList(),
            permissions ?? new[] { "files.read:selected", "files.write:user-approved-output" },
            new PlatformConstraints(new[] { "windows" }, new[] { "x64", "arm64" }),
            (minHostVersion ?? new Version(0, 1, 0)).ToString(), payload,
            (capabilities ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    public static string ToJson(ActionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);
        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new JsonException("Manifest exceeds the size limit.");
        return json;
    }

    /// <summary>Reads discovery metadata only. This never verifies payload bytes or authorizes execution.</summary>
    public static ActionManifest Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new JsonException("Manifest exceeds the size limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        CheckDuplicateProperties(document.RootElement);
        var manifest = JsonSerializer.Deserialize<ActionManifest>(json) ?? throw new JsonException("Manifest is empty.");
        Validate(manifest);
        return manifest;
    }

    private static ManifestAction ToManifestAction(IAction action)
    {
        var d = action.Descriptor;
        return new ManifestAction(
            d.Id,
            d.Title,
            d.Accepts.Select(k => k.ToString().ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal).ToList(),
            d.Description,
            AcceptedMimeTypes: null,
            RequiredCapabilities: d.RequiredCapabilities?.OrderBy(c => c, StringComparer.Ordinal).ToList(),
            RequiresPreview: d.RequiresPreview,
            Parameters: d.Parameters?.Select(ToManifestParameter).ToList(),
            Selection: d.Selection is null ? null : ManifestSelection.FromRequirements(d.Selection),
            Extensions: (action as IFileActionMetadata)?.InputExtensions?.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToArray(),
            ChangesFiles: d.ChangesFiles);
    }

    private static void CheckDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate manifest property.");
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicateProperties(item);
    }

    private static void Validate(ActionManifest manifest)
    {
        if (manifest.SchemaVersion is not ("1.0" or "2.0")) throw new JsonException("Unsupported manifest schema version.");
        RequireText(manifest.Id); RequireText(manifest.Version); RequireText(manifest.DisplayName); RequireText(manifest.Publisher); RequireText(manifest.ProtocolVersion);
        if (manifest.Actions is null || manifest.Actions.Count > 10000) throw new JsonException("Invalid manifest actions.");
        if (manifest.SchemaVersion == "2.0")
        {
            RequireText(manifest.MinHostVersion);
            if (!Version.TryParse(manifest.MinHostVersion, out _) || manifest.Capabilities is null) throw new JsonException("Missing v2 pack metadata.");
            if (manifest.Capabilities.Count > 1000) throw new JsonException("Too many pack capabilities.");
            foreach (string capability in manifest.Capabilities) RequireText(capability);
            if (manifest.Payload is not null) ValidatePayload(manifest.Payload);
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in manifest.Actions)
        {
            if (action is null || !ids.Add(action.Id)) throw new JsonException("Duplicate or missing action.");
            RequireText(action.Id); RequireText(action.Title);
            if (action.InputKinds is null || action.InputKinds.Count == 0 || action.InputKinds.Any(k => k is not ("none" or "files" or "table" or "text")))
                throw new JsonException("Invalid action input kinds.");
            if (manifest.SchemaVersion == "2.0" && (!action.ChangesFiles.HasValue || action.InputKinds.Contains("files") && action.Selection is null))
                throw new JsonException("Missing v2 action metadata.");
            if (action.Extensions is not null && action.Extensions.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 32 || !e.StartsWith('.') || e.IndexOfAny(new[] { '/', '\\', ':', '*', '?' }) >= 0))
                throw new JsonException("Invalid action extensions.");
            if (action.Selection is not null) ValidateSelection(action.Selection);
            if (action.Parameters is not null)
            {
                if (action.Parameters.Count > 1000) throw new JsonException("Too many action parameters.");
                foreach (var parameter in action.Parameters)
                {
                    if (parameter is null) throw new JsonException("Missing action parameter.");
                    RequireText(parameter.Name);
                    if (parameter.Type is not ("string" or "boolean" or "number" or "choice" or "password" or "file" or "color") ||
                        parameter.Type == "password" && parameter.Default is not null)
                        throw new JsonException("Invalid action parameter metadata.");
                }
            }
        }
    }

    private static void ValidateSelection(ManifestSelection selection)
    {
        if (selection.Families is null || selection.Families.Count > 100 || selection.FamilyLimits is { Count: > 100 } || selection.MinItems < 0 ||
            selection.MaxItems.HasValue && selection.MaxItems.Value < selection.MinItems ||
            selection.IdealMinItems.GetValueOrDefault() < 0 || selection.IdealMaxItems.GetValueOrDefault() < 0 ||
            selection.IdealMinItems.HasValue && selection.IdealMaxItems.HasValue && selection.IdealMinItems.Value > selection.IdealMaxItems.Value ||
            selection.IdealMinDistinctFamilies.GetValueOrDefault() < 0)
            throw new JsonException("Invalid selection limits.");
        foreach (string family in selection.Families) RequireText(family);
        if (selection.FamilyLimits is not null)
            foreach (var pair in selection.FamilyLimits)
            {
                RequireText(pair.Key);
                if (pair.Value is null || pair.Value.Min < 0 || pair.Value.Max.HasValue && pair.Value.Max.Value < pair.Value.Min) throw new JsonException("Invalid family limits.");
            }
    }

    private static void ValidatePayload(ManifestPayload payload)
    {
        ValidatePath(payload.EntryAssembly);
        if (!payload.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || payload.SizeBytes < 0 || payload.Files is null || payload.Files.Count is 0 or > 10000)
            throw new JsonException("Invalid payload metadata.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0; bool allSizes = true;
        foreach (var file in payload.Files)
        {
            if (file is null) throw new JsonException("Invalid payload file.");
            ValidatePath(file.Path);
            if (!paths.Add(file.Path) || file.SizeBytes.HasValue && file.SizeBytes.Value < 0 || file.Sha256 is null || file.Sha256.Length != 64 || file.Sha256.Any(c => !Uri.IsHexDigit(c)))
                throw new JsonException("Invalid payload file metadata.");
            if (file.SizeBytes.HasValue)
            {
                if (total > long.MaxValue - file.SizeBytes.Value) throw new JsonException("Invalid payload size.");
                total += file.SizeBytes.Value;
            }
            else allSizes = false;
        }
        if (!paths.Contains(payload.EntryAssembly) || allSizes && total != payload.SizeBytes) throw new JsonException("Payload inventory is inconsistent.");
    }
    private static void ValidatePath(string path)
    {
        RequireText(path);
        if (path.StartsWith('/') || path.Contains('\\')) throw new JsonException("Invalid payload path.");
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => c < ' ' || ":<>\"|?*".Contains(c))) throw new JsonException("Invalid payload path.");
            string device = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" || device.Length == 4 && device[3] is >= '1' and <= '9' &&
                (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)))
                throw new JsonException("Invalid payload path.");
        }
    }
    private static void RequireText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) throw new JsonException("Missing or oversized manifest text.");
    }

    private static ManifestParameter ToManifestParameter(ActionParameterDefinition p) => new(
        p.Name,
        p.Kind switch
        {
            ActionParameterKind.Integer => "number",
            ActionParameterKind.Boolean => "boolean",
            ActionParameterKind.Choice => "choice",
            ActionParameterKind.Password => "password",
            ActionParameterKind.FilePath => "file",
            ActionParameterKind.Color => "color",
            _ => "string"
        },
        p.Title,
        p.Description,
        Required: false,
        Choices: p.Choices?.Select(c => c.Value).ToList(),
        Default: p.Kind == ActionParameterKind.Password ? null : p.DefaultValue);
}
