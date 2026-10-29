using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PowerTrove.Abstractions.Manifest;

/// <summary>
/// Architectural preparation for future action pack extensions.
/// Untrusted third-party execution is out of scope for the current MVP.
/// </summary>
public sealed record ActionManifest(
    [property: JsonRequired][property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonRequired][property: JsonPropertyName("id")] string Id,
    [property: JsonRequired][property: JsonPropertyName("version")] string Version,
    [property: JsonRequired][property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonRequired][property: JsonPropertyName("publisher")] string Publisher,
    [property: JsonRequired][property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonRequired][property: JsonPropertyName("actions")] IReadOnlyList<ManifestAction> Actions,
    [property: JsonPropertyName("permissions")] IReadOnlyList<string>? Permissions = null,
    [property: JsonPropertyName("platformConstraints")] PlatformConstraints? PlatformConstraints = null,
    [property: JsonPropertyName("minHostVersion")] string? MinHostVersion = null,
    [property: JsonPropertyName("payload")] ManifestPayload? Payload = null,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string>? Capabilities = null,
    [property: JsonPropertyName("releaseFingerprint")] string? ReleaseFingerprint = null
)
{
    // Additive presentation metadata keeps the existing constructor/binding contract intact.
    [JsonPropertyName("localizations")]
    public IReadOnlyDictionary<string, ManifestPresentation>? Localizations { get; init; }
}

public sealed record PlatformConstraints(
    [property: JsonPropertyName("os")] IReadOnlyList<string>? Os = null,
    [property: JsonPropertyName("minimumArchitecture")] IReadOnlyList<string>? MinimumArchitecture = null
);

public sealed record ManifestAction(
    [property: JsonRequired][property: JsonPropertyName("id")] string Id,
    [property: JsonRequired][property: JsonPropertyName("title")] string Title,
    [property: JsonRequired][property: JsonPropertyName("inputKinds")] IReadOnlyList<string> InputKinds,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("acceptedMimeTypes")] IReadOnlyList<string>? AcceptedMimeTypes = null,
    [property: JsonPropertyName("requiredCapabilities")] IReadOnlyList<string>? RequiredCapabilities = null,
    [property: JsonPropertyName("requiresPreview")] bool? RequiresPreview = null,
    [property: JsonPropertyName("parameters")] IReadOnlyList<ManifestParameter>? Parameters = null,
    [property: JsonPropertyName("selection")] ManifestSelection? Selection = null,
    [property: JsonPropertyName("extensions")] IReadOnlyList<string>? Extensions = null,
    [property: JsonPropertyName("changesFiles")] bool? ChangesFiles = null
);

public sealed record ManifestPayload(
    [property: JsonRequired][property: JsonPropertyName("entryAssembly")] string EntryAssembly,
    [property: JsonRequired][property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonRequired][property: JsonPropertyName("files")] IReadOnlyList<ManifestPayloadFile> Files);

public sealed record ManifestPayloadFile(
    [property: JsonRequired][property: JsonPropertyName("path")] string Path,
    [property: JsonRequired][property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sizeBytes")] long? SizeBytes = null);

public sealed record ManifestSelection(
    [property: JsonRequired][property: JsonPropertyName("families")] IReadOnlyList<string> Families,
    [property: JsonRequired][property: JsonPropertyName("minItems")] int MinItems,
    [property: JsonPropertyName("maxItems")] int? MaxItems,
    [property: JsonPropertyName("idealMinItems")] int? IdealMinItems,
    [property: JsonPropertyName("idealMaxItems")] int? IdealMaxItems,
    [property: JsonRequired][property: JsonPropertyName("allowOtherItems")] bool AllowOtherItems,
    [property: JsonPropertyName("idealNeedsEveryFamily")] bool IdealNeedsEveryFamily = false,
    [property: JsonPropertyName("familyLimits")] IReadOnlyDictionary<string, ManifestFamilyLimit>? FamilyLimits = null,
    [property: JsonPropertyName("idealMinDistinctFamilies")] int? IdealMinDistinctFamilies = null)
{
    public static ManifestSelection FromRequirements(SelectionRequirements requirements) => new(
        new List<string>(new SortedSet<string>(requirements.Families, StringComparer.OrdinalIgnoreCase)),
        requirements.MinItems, requirements.MaxItems, requirements.IdealMinItems, requirements.IdealMaxItems,
        requirements.AllowOtherItems, requirements.IdealNeedsEveryFamily, CopyLimits(requirements.FamilyLimits), requirements.IdealMinDistinctFamilies);

    public SelectionRequirements ToRequirements() => new(
        new HashSet<string>(Families, StringComparer.OrdinalIgnoreCase), MinItems, MaxItems, AllowOtherItems,
        IdealMinItems, IdealMaxItems, IdealNeedsEveryFamily, RestoreLimits(FamilyLimits), IdealMinDistinctFamilies);

    private static IReadOnlyDictionary<string, ManifestFamilyLimit>? CopyLimits(IReadOnlyDictionary<string, FamilyLimit>? limits)
    {
        if (limits is null) return null;
        var result = new SortedDictionary<string, ManifestFamilyLimit>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in limits) result.Add(pair.Key, new(pair.Value.Min, pair.Value.Max));
        return result;
    }
    private static IReadOnlyDictionary<string, FamilyLimit>? RestoreLimits(IReadOnlyDictionary<string, ManifestFamilyLimit>? limits)
    {
        if (limits is null) return null;
        var result = new Dictionary<string, FamilyLimit>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in limits) result.Add(pair.Key, new(pair.Value.Min, pair.Value.Max));
        return result;
    }
}

public sealed record ManifestFamilyLimit(
    [property: JsonRequired][property: JsonPropertyName("min")] int Min,
    [property: JsonPropertyName("max")] int? Max);

public sealed record ManifestParameter(
    [property: JsonRequired][property: JsonPropertyName("name")] string Name,
    [property: JsonRequired][property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("required")] bool Required = false,
    [property: JsonPropertyName("choices")] IReadOnlyList<string>? Choices = null,
    [property: JsonPropertyName("default")] string? Default = null,
    [property: JsonPropertyName("editor")] string? Editor = null,
    [property: JsonPropertyName("minimum")] int? Minimum = null,
    [property: JsonPropertyName("maximum")] int? Maximum = null,
    [property: JsonPropertyName("placeholder")] string? Placeholder = null,
    [property: JsonPropertyName("fileTypes")] IReadOnlyList<string>? FileTypes = null,
    [property: JsonPropertyName("mustExist")] bool? MustExist = null,
    [property: JsonPropertyName("visibleWhenParameter")] string? VisibleWhenParameter = null,
    [property: JsonPropertyName("visibleWhenValue")] string? VisibleWhenValue = null,
    [property: JsonPropertyName("choiceTitles")] IReadOnlyDictionary<string, string>? ChoiceTitles = null
);
