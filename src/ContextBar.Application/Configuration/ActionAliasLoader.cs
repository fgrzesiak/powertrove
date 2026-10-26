using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ContextBar.Application.Configuration;

public static class ActionAliasLoader
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? LoadAliases(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            string json = File.ReadAllText(filePath);
            var aliases = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);

            if (aliases == null)
            {
                return null;
            }

            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in aliases)
            {
                result[kvp.Key] = kvp.Value;
            }

            return result;
        }
        catch
        {
            // Fail silently on loading aliases in this context, or log it if a logger is provided
            return null;
        }
    }
}
