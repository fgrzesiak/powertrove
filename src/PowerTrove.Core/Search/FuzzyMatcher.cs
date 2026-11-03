using System;
using System.Globalization;
using System.Text;

namespace PowerTrove.Core.Search;

/// <summary>Culture-independent, bounded matching of human search words. No external services or user data.</summary>
public static class FuzzyMatcher
{
    /// <summary>Normalizes case, accents, common ligatures and punctuation into comparable words.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var decomposed = text.Replace("ß", "ss", StringComparison.OrdinalIgnoreCase)
            .Replace("ẞ", "ss", StringComparison.Ordinal)
            .Replace("æ", "ae", StringComparison.OrdinalIgnoreCase)
            .Replace("œ", "oe", StringComparison.OrdinalIgnoreCase)
            .Replace("ø", "o", StringComparison.OrdinalIgnoreCase)
            .Replace("ł", "l", StringComparison.OrdinalIgnoreCase)
            .Normalize(NormalizationForm.FormKC) // Normalize full-width and compatibility characters.
            .Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        bool separator = false;
        foreach (char character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;
            if (char.IsLetterOrDigit(character))
            {
                if (separator && result.Length > 0) result.Append(' ');
                result.Append(char.ToLowerInvariant(character));
                separator = false;
            }
            else separator = true;
        }
        return result.ToString();
    }

    public static int CalculateScore(string query, string target)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(target) || query.Length > 256)
            return 0;
        string normalizedQuery = Normalize(query), normalizedTarget = Normalize(target);
        if (normalizedQuery.Length == 0 || normalizedTarget.Length == 0) return 0;
        if (normalizedQuery == normalizedTarget) return 100;
        if (normalizedTarget.StartsWith(normalizedQuery, StringComparison.Ordinal)) return 90;

        string[] queryWords = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] targetWords = normalizedTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int sum = 0;
        foreach (var queryWord in queryWords)
        {
            int best = 0;
            foreach (var targetWord in targetWords)
            {
                int score = EvaluateWord(queryWord, targetWord);
                if (score > best) best = score;
                if (best == 100) break;
            }
            // All meaningful query words must be accounted for: no unrelated filler results.
            if (best == 0) return 0;
            sum += best;
        }

        int average = sum / queryWords.Length;
        return average == 100 ? 100 : Math.Min(89, Math.Max(1, average));
    }

    private static int EvaluateWord(string query, string target)
    {
        if (query == target) return 100;
        if (target.StartsWith(query, StringComparison.Ordinal)) return 90;
        // "namen" finds "Dateinamen", without accepting tiny accidental substrings.
        if (query.Length >= 3 && target.Contains(query, StringComparison.Ordinal))
            return 72;
        int shortest = Math.Min(query.Length, target.Length);
        // Short words are often format names (webp/webm, tif/tiff): they must match exactly.
        int allowance = shortest switch { >= 7 => 3, 6 => 2, 5 => 1, _ => 0 };
        if (allowance == 0 || Math.Abs(query.Length - target.Length) > allowance) return 0;
        int distance = EditDistance(query, target);
        return distance > allowance ? 0 : Math.Max(1, (int)((1.0 - (double)distance / Math.Max(query.Length, target.Length)) * 100));
    }

    // Optimal-string-alignment edit distance: one adjacent transposition is one typo.
    private static int EditDistance(string query, string target)
    {
        int[] prevPrev = new int[target.Length + 1], prev = new int[target.Length + 1], curr = new int[target.Length + 1];
        for (int j = 0; j <= target.Length; j++) prev[j] = j;
        for (int i = 1; i <= query.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= target.Length; j++)
            {
                int cost = query[i - 1] == target[j - 1] ? 0 : 1;
                int value = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && query[i - 1] == target[j - 2] && query[i - 2] == target[j - 1])
                    value = Math.Min(value, prevPrev[j - 2] + 1);
                curr[j] = value;
            }
            (prevPrev, prev, curr) = (prev, curr, prevPrev);
        }
        return prev[target.Length];
    }
}
