using PowerTrove.Abstractions.Ocr;

namespace PowerTrove.Platform.Windows.Imaging;

/// <summary>Lines read by one OCR engine and that engine's language; null when nothing was read.</summary>
public sealed record RecognizedPage(IReadOnlyList<RecognizedLine> Lines, string? Language);

/// <summary>
/// Bounded, offline ranking of installed OCR engines. This is an estimate, not a language model:
/// script agreement and common short words help distinguish engines that read the same screenshot.
/// No image content is persisted or sent outside the local process.
/// </summary>
public static class OcrLanguageHeuristics
{
    private static readonly IReadOnlyDictionary<string, string[]> CommonWords =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = ["the", "and", "with", "for", "this", "from", "you", "your", "file", "open", "save", "settings"],
            ["de"] = ["der", "die", "das", "und", "mit", "für", "nicht", "eine", "einen", "datei", "öffnen", "speichern"],
            ["fr"] = ["les", "des", "une", "avec", "pour", "dans", "sur", "vous", "fichier", "enregistrer"],
            ["es"] = ["los", "las", "una", "con", "para", "del", "archivo", "guardar", "abrir", "este"],
            ["pt"] = ["uma", "com", "para", "dos", "das", "arquivo", "salvar", "abrir", "este"],
            ["it"] = ["una", "con", "per", "della", "file", "salva", "apri", "questo"],
            ["pl"] = ["jest", "oraz", "dla", "plik", "otwórz", "zapisz", "nie", "ustawienia"],
            ["tr"] = ["bir", "için", "dosya", "aç", "kaydet", "ayarlar", "ile", "değil"],
            ["id"] = ["dan", "untuk", "dengan", "file", "simpan", "buka", "pengaturan"],
            ["nl"] = ["het", "een", "met", "voor", "bestand", "openen", "opslaan"],
        };

    public static double Score(string language, IReadOnlyList<RecognizedLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var content = string.Join(" ", lines.Take(500).Select(line => line.Text));
        if (content.Length > 20000) content = content[..20000];
        int letters = 0, glyphs = 0, japanese = 0, hangul = 0, cyrillic = 0,
            arabic = 0, devanagari = 0, han = 0;
        foreach (char character in content)
        {
            if (!char.IsLetterOrDigit(character)) continue;
            glyphs++;
            if (char.IsLetter(character)) letters++;
            if (character is >= '\u3040' and <= '\u30ff') japanese++;
            if (character is >= '\uac00' and <= '\ud7af') hangul++;
            if (character is >= '\u0400' and <= '\u052f') cyrillic++;
            if (character is >= '\u0600' and <= '\u06ff') arabic++;
            if (character is >= '\u0900' and <= '\u097f') devanagari++;
            if (character is >= '\u4e00' and <= '\u9fff') han++;
        }
        if (glyphs == 0) return double.NegativeInfinity;
        var primary = language.Split('-')[0].ToLowerInvariant();
        double score = Math.Min(letters, 400) * .035;
        double nonLatin = japanese + hangul + cyrillic + arabic + devanagari + han;
        if (nonLatin > 0)
        {
            double match = primary switch
            {
                "ja" => japanese + han * .8,
                "zh" => han,
                "ko" => hangul + han * .3,
                "ru" or "uk" or "bg" => cyrillic,
                "ar" or "fa" or "ur" => arabic,
                "hi" or "mr" => devanagari,
                _ => 0
            };
            // A wrong-script engine can output convincing-looking garbage.
            score += 24 * match / glyphs - 20 * (nonLatin - match) / glyphs;
        }
        else if (primary is "ja" or "zh" or "ko" or "ru" or "uk" or "ar" or "hi")
            score -= 6;
        if (CommonWords.TryGetValue(primary, out var words))
        {
            var tokens = content.ToLowerInvariant().Split(
                [' ', '\r', '\n', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '/'],
                StringSplitOptions.RemoveEmptyEntries);
            var dictionary = new HashSet<string>(words, StringComparer.Ordinal);
            score += Math.Min(12, tokens.Count(dictionary.Contains)) * 3;
        }
        // Very short, fragmented OCR output is less reliable than coherent lines.
        score += Math.Min(lines.Count(line => line.Text.Length >= 4), 15) * .4;
        return score;
    }

    public static string[] CandidateLanguages(IEnumerable<string> installed, IEnumerable<string> profile,
        int maxCandidates = 16)
    {
        if (maxCandidates is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        var tags = installed.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var priority = profile.Concat(["en", "de", "ja", "zh", "ko", "ru", "ar", "hi",
            "es", "fr", "pt", "it", "pl", "tr", "id", "nl"]);
        var result = new List<string>(maxCandidates);
        void Add(string tag)
        {
            if (result.Count == maxCandidates) return;
            var installedTag = tags.FirstOrDefault(value =>
                value.Equals(tag, StringComparison.OrdinalIgnoreCase)) ??
                tags.FirstOrDefault(value => value.StartsWith(tag + "-", StringComparison.OrdinalIgnoreCase));
            if (installedTag is not null && !result.Contains(installedTag, StringComparer.OrdinalIgnoreCase))
                result.Add(installedTag);
        }
        foreach (string tag in priority) Add(tag);
        foreach (string tag in tags) Add(tag);
        return result.ToArray();
    }
}
