using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PowerTrove.Abstractions.Manifest;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManifestPresentation(
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("actions")] IReadOnlyDictionary<string, ManifestActionPresentation> Actions);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManifestActionPresentation(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("parameters")] IReadOnlyDictionary<string, ManifestParameterPresentation>? Parameters = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManifestParameterPresentation(
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("placeholder")] string? Placeholder = null,
    [property: JsonPropertyName("choiceTitles")] IReadOnlyDictionary<string, string>? ChoiceTitles = null);

/// <summary>Pure presentation views. Never use a translated view for signatures or manifest fingerprints.</summary>
public static class ManifestLocalization
{
    public static ActionManifest ForCulture(ActionManifest manifest, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Localizations is null) return manifest;
        for (var current = culture ?? CultureInfo.CurrentUICulture; current.Name.Length != 0; current = current.Parent)
        {
            var presentation = manifest.Localizations.FirstOrDefault(p => p.Key.Equals(current.Name, StringComparison.OrdinalIgnoreCase)).Value;
            if (presentation is not null) return Apply(manifest, presentation);
        }
        return manifest;
    }

    /// <summary>Captures existing pack resources while refusing language-dependent execution contracts.</summary>
    public static ActionManifest WithTranslation(ActionManifest manifest, string language, ActionManifest translated)
    {
        var presentation = new ManifestPresentation(translated.DisplayName, translated.Actions.ToDictionary(a => a.Id, a =>
            new ManifestActionPresentation(a.Title, a.Description, a.Parameters?.ToDictionary(p => p.Name, p =>
                new ManifestParameterPresentation(p.Title, p.Description, p.Placeholder, p.ChoiceTitles), StringComparer.Ordinal)), StringComparer.Ordinal));
        var candidate = Apply(manifest with { Localizations = null }, presentation);
        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(candidate), JsonSerializer.SerializeToNode(translated with { Localizations = null })))
            throw new JsonException("A translation changes the pack execution contract.");
        var localizations = new SortedDictionary<string, ManifestPresentation>(StringComparer.Ordinal);
        if (manifest.Localizations is not null) foreach (var pair in manifest.Localizations) localizations.Add(pair.Key, pair.Value);
        localizations.Add(language, presentation);
        var result = manifest with { Localizations = localizations };
        Validate(result);
        return result;
    }

    private static ActionManifest Apply(ActionManifest manifest, ManifestPresentation presentation) => manifest with
    {
        DisplayName = presentation.DisplayName,
        Actions = manifest.Actions.Select(action => !presentation.Actions.TryGetValue(action.Id, out var text) ? action : action with
        {
            Title = text.Title, Description = text.Description ?? action.Description,
            Parameters = action.Parameters?.Select(parameter => text.Parameters?.TryGetValue(parameter.Name, out var label) != true ? parameter : parameter with
            {
                Title = label!.Title ?? parameter.Title, Description = label.Description ?? parameter.Description,
                Placeholder = label.Placeholder ?? parameter.Placeholder,
                ChoiceTitles = label.ChoiceTitles is null ? parameter.ChoiceTitles : parameter.Choices?.ToDictionary(value => value,
                    value => label.ChoiceTitles?.GetValueOrDefault(value) ?? parameter.ChoiceTitles?.GetValueOrDefault(value) ?? value, StringComparer.Ordinal)
            }).ToArray()
        }).ToArray()
    };

    internal static void Validate(ActionManifest manifest)
    {
        if (manifest.Localizations is null) return;
        if (manifest.Localizations.Count > 16) throw new JsonException("Too many manifest languages.");
        var actions = manifest.Actions.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in manifest.Localizations)
        {
            if (pair.Key.Length is < 2 or > 35 || !languages.Add(pair.Key)) throw new JsonException("Invalid manifest language.");
            try { if (CultureInfo.GetCultureInfo(pair.Key).Name.Length == 0) throw new JsonException("Invalid manifest language."); }
            catch (CultureNotFoundException) { throw new JsonException("Invalid manifest language."); }
            var text = pair.Value;
            if (text is null || text.Actions is null || text.Actions.Count > manifest.Actions.Count) throw new JsonException("Invalid localized pack metadata.");
            Required(text.DisplayName);
            foreach (var item in text.Actions)
            {
                if (!actions.TryGetValue(item.Key, out var action) || item.Value is null) throw new JsonException("Unknown localized action.");
                Required(item.Value.Title); Optional(item.Value.Description);
                if (item.Value.Parameters is null) continue;
                if (item.Value.Parameters.Count > (action.Parameters?.Count ?? 0)) throw new JsonException("Invalid localized parameters.");
                foreach (var entry in item.Value.Parameters)
                {
                    var parameter = action.Parameters?.SingleOrDefault(p => p.Name == entry.Key);
                    if (parameter is null || entry.Value is null) throw new JsonException("Unknown localized parameter.");
                    Optional(entry.Value.Title); Optional(entry.Value.Description); Optional(entry.Value.Placeholder);
                    if (entry.Value.ChoiceTitles is null) continue;
                    if (entry.Value.ChoiceTitles.Count > (parameter.Choices?.Count ?? 0)) throw new JsonException("Invalid localized choices.");
                    foreach (var choice in entry.Value.ChoiceTitles)
                    {
                        if (parameter.Choices?.Contains(choice.Key, StringComparer.Ordinal) != true) throw new JsonException("Unknown localized choice.");
                        Required(choice.Value);
                    }
                }
            }
        }
    }

    private static void Required(string? value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) throw new JsonException("Invalid localized text."); }
    private static void Optional(string? value) { if (value is not null && value.Length > 4096) throw new JsonException("Oversized localized text."); }
}
