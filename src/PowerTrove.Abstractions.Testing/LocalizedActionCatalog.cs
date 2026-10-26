using System.Collections;
using System.Globalization;
using System.Resources;
using PowerTrove.Abstractions;

namespace PowerTrove.Abstractions.Testing;

/// <summary>Shared English/German metadata checks for packs, without activating native engines.</summary>
public static class LocalizedActionCatalog
{
    // Exact, case-sensitive values only. Never allow whole action IDs or arbitrary text patterns.
    private static readonly IReadOnlyDictionary<string, string> SharedTerms = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PDF"] = "PDF format name.",
        ["ZIP"] = "ZIP archive format name.",
        ["JSON"] = "JSON format name.",
        ["CSV"] = "CSV format name.",
        ["Markdown"] = "Markdown format name.",
        ["SQL"] = "SQL language name.",
        ["MP3"] = "MP3 codec name.",
        ["GIF"] = "GIF format name.",
        ["JPEG"] = "JPEG format name.",
        ["PNG"] = "PNG format name.",
        ["WebP"] = "WebP format name.",
        ["Format"] = "The noun is spelled identically in English and German.",
        ["Video"] = "The noun is spelled identically in English and German.",
        ["SHA-256"] = "Standard cryptographic algorithm identifier, never translated.",
        ["SHA-512"] = "Standard cryptographic algorithm identifier, never translated.",
        ["TIFF"] = "TIFF format name.",
        ["BMP"] = "BMP format name.",
        ["ICO"] = "Windows icon format name.",
        ["1920x1080 (HD)"] = "Fixed dimensions and the international HD label.",
        ["1280x720 (Web)"] = "Fixed dimensions and the international Web label.",
        ["1:1"] = "Language-independent aspect ratio.",
        ["4:3"] = "Language-independent aspect ratio.",
        ["3:2"] = "Language-independent aspect ratio.",
        ["16:9"] = "Language-independent aspect ratio.",
        ["9:16"] = "Language-independent aspect ratio.",
        ["4:5"] = "Language-independent aspect ratio.",
        ["Favicon (16, 32, 48)"] = "Standard icon profile name and dimensions.",
        ["Windows (16–256)"] = "Windows product/profile name and dimensions.",
        ["Horizontal"] = "The orientation adjective is identical in both languages.",
        ["Diagonal"] = "The orientation adjective is identical in both languages.",
        ["Position"] = "The noun is identical in both languages.",
        ["Text"] = "The noun is identical in both languages.",
        ["Normal"] = "The sensitivity adjective is identical in both languages.",
        ["180°"] = "Language-independent rotation measurement.",
        ["270°"] = "Language-independent rotation measurement.",
        ["A4"] = "Standard paper size identifier.",
        ["Letter"] = "Standard paper size identifier.",
        ["US Letter"] = "Standard paper size identifier.",
        ["2"] = "Fixed N-up pages-per-sheet count.",
        ["4"] = "Fixed N-up pages-per-sheet count.",
        ["n"] = "Page-number notation.",
        ["n / N"] = "Current/total page-number notation.",
        ["Windows (CRLF)"] = "Platform name and line-ending notation.",
        ["Unix (LF)"] = "Platform name and line-ending notation.",
        ["PostgreSQL"] = "Database product name.",
        ["MySQL"] = "Database product name.",
        ["SQLite"] = "Database product name.",
        ["SQL Server"] = "Database product name."
    };

    /// <summary>Returns action IDs and field names for missing, untranslated or culture-sticky metadata.</summary>
    public static IReadOnlyList<string> FindProblems(Func<IReadOnlyList<IActionPack>> createPacks)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var englishPacks = createPacks();
            var english = Capture(englishPacks);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var german = Capture(createPacks());
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var englishAgain = Capture(createPacks());
            var problems = new List<string>();
            foreach (var (field, text) in english)
            {
                if (string.IsNullOrWhiteSpace(text)) problems.Add(field + ": empty English text");
                if (!german.TryGetValue(field, out string? translated) || string.IsNullOrWhiteSpace(translated))
                    problems.Add(field + ": missing German text");
                else if (text == translated && !SharedTerms.ContainsKey(text)) problems.Add(field + ": untranslated");
                if (!englishAgain.TryGetValue(field, out string? repeated) || text != repeated)
                    problems.Add(field + ": culture-dependent English result");
            }
            foreach (string field in german.Keys.Except(english.Keys)) problems.Add(field + ": German-only action or field");

            // Exact German-only resource values must never leak back into the English catalog.
            foreach (var assembly in englishPacks.SelectMany(p => p.Actions).Select(a => a.GetType().Assembly)
                .Concat(englishPacks.Select(p => p.GetType().Assembly)).Distinct())
            {
                var prefixes = englishPacks.SelectMany(p => p.Actions.Where(a => p.GetType().Assembly == assembly || a.GetType().Assembly == assembly))
                    .Select(a => a.Descriptor.Id + ".").ToArray();
                // Linked host recipe code has the desktop resource name in the test assembly.
                // Synthetic controls have no resources but still receive every metadata check.
                foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".Resources.Strings.resources", StringComparison.Ordinal)))
                {
                    var manager = new ResourceManager(resource[..^".resources".Length], assembly);
                    var neutral = Values(manager.GetResourceSet(CultureInfo.InvariantCulture, true, false));
                    var translated = Values(manager.GetResourceSet(CultureInfo.GetCultureInfo("de"), true, false));
                    translated.ExceptWith(neutral);
                    foreach (var (field, text) in english.Where(f => prefixes.Any(prefix => f.Key.StartsWith(prefix, StringComparison.Ordinal))))
                        if (translated.Contains(text)) problems.Add(field + ": German resource leaked into English");
                }
            }
            return problems.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static Dictionary<string, string> Capture(IReadOnlyList<IActionPack> packs)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var action in packs.SelectMany(p => p.Actions))
        {
            var descriptor = action.Descriptor;
            string id = descriptor.Id;
            result.Add(id + ".Title", descriptor.Title);
            result.Add(id + ".Description", descriptor.Description);
            foreach (var parameter in descriptor.Parameters ?? Array.Empty<ActionParameterDefinition>())
            {
                string field = id + ".Parameters[" + parameter.Name + "]";
                result.Add(field + ".Title", parameter.Title);
                if (parameter.Description is not null) result.Add(field + ".Description", parameter.Description);
                foreach (var choice in parameter.Choices ?? Array.Empty<ActionParameterChoice>())
                    result.Add(field + ".Choices[" + choice.Value + "].Title", choice.Title);
            }
        }
        return result;
    }

    private static HashSet<string> Values(ResourceSet? resources) => resources is null ? new(StringComparer.Ordinal) :
        resources.Cast<DictionaryEntry>().Where(e => e.Value is string).Select(e => (string)e.Value!).ToHashSet(StringComparer.Ordinal);
}
