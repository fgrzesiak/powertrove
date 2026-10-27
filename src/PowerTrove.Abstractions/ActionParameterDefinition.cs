using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PowerTrove.Abstractions;

public enum ActionParameterKind
{
    Text,
    Integer,
    Boolean,
    Choice,
    Password,
    FilePath,
    Color
}

public sealed record ActionParameterChoice(string Value, string Title);

/// <summary>Host editor hint; the stored/wire value retains its existing parameter type.</summary>
public enum ActionParameterEditor { Default, MultiSelect }

/// <summary>
/// Declarative description of one user-adjustable action option. The host renders
/// a generic options dialog from these definitions; the same shape is used by the
/// action pack manifest (<c>parameters</c>), so built-in and future packaged actions
/// share one schema.
/// </summary>
public sealed record ActionParameterDefinition(
    string Name,
    ActionParameterKind Kind,
    string Title,
    string? Description = null,
    string? DefaultValue = null,
    int? Minimum = null,
    int? Maximum = null,
    IReadOnlyList<ActionParameterChoice>? Choices = null,
    string? Placeholder = null,
    IReadOnlyList<string>? FileTypes = null,
    bool? MustExist = null,
    string? VisibleWhenParameter = null,
    string? VisibleWhenValue = null,
    ActionParameterEditor Editor = ActionParameterEditor.Default)
{
    public static ActionParameterDefinition Integer(string name, string title, int defaultValue, int minimum, int maximum, string? description = null) =>
        new(name, ActionParameterKind.Integer, title, description,
            defaultValue.ToString(CultureInfo.InvariantCulture), minimum, maximum);

    public static ActionParameterDefinition Boolean(string name, string title, bool defaultValue, string? description = null) =>
        new(name, ActionParameterKind.Boolean, title, description, defaultValue ? "true" : "false");

    public static ActionParameterDefinition Choice(string name, string title, string defaultValue, IReadOnlyList<ActionParameterChoice> choices, string? description = null)
    {
        if (!choices.Any(c => c.Value == defaultValue))
            throw new ArgumentException("Default value must be one of the choices.", nameof(defaultValue));
        return new(name, ActionParameterKind.Choice, title, description, defaultValue, Choices: choices);
    }

    public static ActionParameterDefinition Text(string name, string title, string defaultValue, string? description = null, string? placeholder = null) =>
        new(name, ActionParameterKind.Text, title, description, defaultValue, Placeholder: placeholder);

    /// <summary>Choices arrive with the preview. Empty text means all; an array means an explicit selection.</summary>
    public static ActionParameterDefinition MultiSelect(string name, string title, string? description = null) =>
        Text(name, title, string.Empty, description) with { Editor = ActionParameterEditor.MultiSelect };

    public static ActionParameterDefinition Password(string name, string title, string? description = null) =>
        new(name, ActionParameterKind.Password, title, description);

    public static ActionParameterDefinition FilePath(string name, string title, IReadOnlyList<string> fileTypes, bool mustExist = true, string? description = null) =>
        new(name, ActionParameterKind.FilePath, title, description, FileTypes: fileTypes, MustExist: mustExist);

    public static ActionParameterDefinition Color(string name, string title, string defaultValue, string? description = null) =>
        new(name, ActionParameterKind.Color, title, description, defaultValue);

    public ActionParameterDefinition VisibleWhen(string parameter, string value) => this with { VisibleWhenParameter = parameter, VisibleWhenValue = value };
}

/// <summary>
/// Typed, validated view over <see cref="ActionParameters"/>. Unknown, missing or
/// malformed values fall back to the declared default; integers are clamped to the
/// declared range; choices outside the declared set are rejected. Actions therefore
/// never have to trust raw strings from preferences or the options dialog.
/// </summary>
public sealed class ActionParameterValues
{
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly Dictionary<string, ActionParameterDefinition> _definitions;

    private ActionParameterValues(IReadOnlyDictionary<string, string> values, IEnumerable<ActionParameterDefinition> definitions)
    {
        _values = values;
        _definitions = definitions.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
    }

    public static ActionParameterValues From(ActionParameters? parameters, IReadOnlyList<ActionParameterDefinition>? definitions)
    {
        IReadOnlyDictionary<string, string> values = parameters is DictionaryParameters dict
            ? new Dictionary<string, string>(dict.Values, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new ActionParameterValues(values, definitions ?? Array.Empty<ActionParameterDefinition>());
    }

    public int GetInt32(string name)
    {
        var definition = Get(name, ActionParameterKind.Integer);
        int fallback = int.Parse(definition.DefaultValue ?? "0", CultureInfo.InvariantCulture);
        int value = TryRaw(name, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
        if (definition.Minimum is int min && value < min) value = min;
        if (definition.Maximum is int max && value > max) value = max;
        return value;
    }

    public bool GetBoolean(string name)
    {
        var definition = Get(name, ActionParameterKind.Boolean);
        if (TryRaw(name, out var raw) && bool.TryParse(raw, out var parsed))
            return parsed;
        return string.Equals(definition.DefaultValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    public string GetChoice(string name)
    {
        var definition = Get(name, ActionParameterKind.Choice);
        if (TryRaw(name, out var raw) && definition.Choices!.Any(c => string.Equals(c.Value, raw, StringComparison.OrdinalIgnoreCase)))
            return definition.Choices!.First(c => string.Equals(c.Value, raw, StringComparison.OrdinalIgnoreCase)).Value;
        return definition.DefaultValue!;
    }

    public string GetText(string name)
    {
        var definition = Get(name, ActionParameterKind.Text);
        return TryRaw(name, out var raw) ? raw.Trim() : definition.DefaultValue ?? string.Empty;
    }

    public string GetPassword(string name)
    {
        var definition = Get(name, ActionParameterKind.Password);
        return TryRaw(name, out var raw) ? raw : string.Empty;
    }

    public string GetFilePath(string name)
    {
        var definition = Get(name, ActionParameterKind.FilePath);
        return TryRaw(name, out var raw) ? raw.Trim() : string.Empty;
    }

    public (byte R, byte G, byte B) GetColor(string name)
    {
        var definition = Get(name, ActionParameterKind.Color);
        string hex = TryRaw(name, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : definition.DefaultValue!;
        if (hex.StartsWith("#")) hex = hex.Substring(1);
        if (hex.Length == 6 &&
            byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return (r, g, b);
        }

        // Fallback to default
        string defaultHex = definition.DefaultValue!;
        if (defaultHex.StartsWith("#")) defaultHex = defaultHex.Substring(1);
        byte dr = byte.Parse(defaultHex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte dg = byte.Parse(defaultHex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte db = byte.Parse(defaultHex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (dr, dg, db);
    }

    public bool TryRaw(string name, out string raw)
    {
        if (_values.TryGetValue(name, out var value) && value is not null)
        {
            raw = value;
            return true;
        }
        raw = string.Empty;
        return false;
    }

    private ActionParameterDefinition Get(string name, ActionParameterKind kind)
    {
        if (!_definitions.TryGetValue(name, out var definition))
            throw new InvalidOperationException($"Parameter '{name}' is not declared by this action.");
        if (definition.Kind != kind)
            throw new InvalidOperationException($"Parameter '{name}' is declared as {definition.Kind}, not {kind}.");
        return definition;
    }
}
