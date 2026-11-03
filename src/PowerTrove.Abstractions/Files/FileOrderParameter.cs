using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerTrove.Abstractions.Files;

/// <summary>
/// Lets users choose which selected files an action combines and in which order, through a
/// <see cref="ActionParameterDefinition.FileOrder"/> parameter. Values are opaque keys derived from the paths,
/// so the parameter carries no names; labels exist only in the preview's choice set.
/// </summary>
public static class FileOrderParameter
{
    /// <summary>The choices for the options dialog, in the default order.</summary>
    public static ActionParameterChoiceSet Choices(string parameterName, IReadOnlyList<SelectedFile> files, string? description = null)
    {
        var keys = Keys(files.Select(f => f.Path ?? f.Name).ToArray());
        var choices = files.Take(ActionParameterChoiceSet.MaximumChoices)
            .Select((f, i) => new ActionParameterChoice(keys[i], Label(f.Name))).ToArray();
        return new(parameterName, Hash(string.Join("|", keys)), choices, description);
    }

    /// <summary>True for blank text (all files) or a list of distinct keys.</summary>
    public static bool IsValid(string? value) => TryParse(value, out _);

    /// <summary>
    /// The chosen files in the chosen order. Blank text, invalid text and a list that names none of the
    /// files (for example one written for another selection) keep all files in their default order.
    /// </summary>
    public static IReadOnlyList<T> Apply<T>(IReadOnlyList<T> items, Func<T, SelectedFile> file, string? value)
    {
        if (!TryParse(value, out var order) || order is null) return items;
        var keys = Keys(items.Select(i => file(i).Path ?? file(i).Name).ToArray());
        var byKey = new Dictionary<string, T>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++) byKey[keys[i]] = items[i];
        var result = order.Where(byKey.ContainsKey).Select(k => byKey[k]).ToArray();
        return result.Length == 0 ? items : result;
    }

    private static bool TryParse(string? value, out IReadOnlyList<string>? order)
    {
        order = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.Length > ActionParameterChoiceSet.MaximumSelectionLength) return false;
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 2 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            var list = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } key || list.Contains(key, StringComparer.Ordinal)) return false;
                list.Add(key);
            }
            order = list;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string[] Keys(IReadOnlyList<string> paths)
    {
        var keys = new string[paths.Count];
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < paths.Count; i++)
        {
            string key = Hash(Path.GetFullPath(paths[i]).ToUpperInvariant());
            for (int n = 2; !used.Add(key); n++) key = Hash(paths[i].ToUpperInvariant() + "#" + n);
            keys[i] = key;
        }
        return keys;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 6).ToLowerInvariant();

    private static string Label(string name) => name.Length <= ActionParameterChoiceSet.MaximumChoiceLength ? name : name[..(ActionParameterChoiceSet.MaximumChoiceLength - 1)] + "…";
}
