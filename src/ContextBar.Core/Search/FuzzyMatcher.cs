using System;

namespace ContextBar.Core.Search;

public static class FuzzyMatcher
{
    public static int CalculateScore(string query, string target)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(target))
        {
            return 0;
        }

        query = query.Trim();
        target = target.Trim();

        // Exact match
        if (string.Equals(query, target, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        // Prefix match
        if (target.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 90;
        }

        // Word-level matching
        string[] queryWords = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string[] targetWords = target.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        int totalScore = 0;
        int matchCount = 0;

        foreach (var qWord in queryWords)
        {
            int bestWordScore = 0;
            foreach (var tWord in targetWords)
            {
                int score = EvaluateWord(qWord, tWord);
                if (score > bestWordScore)
                {
                    bestWordScore = score;
                }
            }
            totalScore += bestWordScore;
            if (bestWordScore > 0)
            {
                matchCount++;
            }
        }

        // Must match all query words to get a decent score, otherwise heavily penalize
        if (matchCount < queryWords.Length)
        {
            return 0;
        }

        int averageScore = totalScore / queryWords.Length;

        // If average score is 100, we should return 100 (e.g. "convert webp" against "Convert to WebP")
        if (averageScore == 100) return 100;

        // Return a score between 1 and 89 for fuzzy matches
        return Math.Min(89, Math.Max(1, averageScore));
    }

    private static int EvaluateWord(string qWord, string tWord)
    {
        if (string.Equals(qWord, tWord, StringComparison.OrdinalIgnoreCase))
        {
            return 100; // Exact word match
        }

        if (tWord.StartsWith(qWord, StringComparison.OrdinalIgnoreCase))
        {
            return 90; // Prefix word match
        }

        // Fuzzy match via Levenshtein
        int distance = LevenshteinDistance(qWord.ToLowerInvariant(), tWord.ToLowerInvariant());
        int maxLength = Math.Max(qWord.Length, tWord.Length);

        // If distance is too large relative to word length, it's not a match
        if (distance > 3 || (qWord.Length <= 4 && distance > 0) || (qWord.Length == 5 && distance > 1) || (qWord.Length == 6 && distance > 2) || (qWord.Length > 6 && distance > 3))
        {
            return 0; // Strict to prevent false positives like 'cnvrt' matching, but allow 'cpress' for 'compress' (dist 2 on len 6)
        }

        // Score based on distance (e.g. 1 error in 5 chars = 80%)
        int score = (int)((1.0 - (double)distance / maxLength) * 100);
        return score;
    }

    private static int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
        if (string.IsNullOrEmpty(t)) return s.Length;

        int[] v0 = new int[t.Length + 1];
        int[] v1 = new int[t.Length + 1];

        for (int i = 0; i < v0.Length; i++)
            v0[i] = i;

        for (int i = 0; i < s.Length; i++)
        {
            v1[0] = i + 1;

            for (int j = 0; j < t.Length; j++)
            {
                int cost = (s[i] == t[j]) ? 0 : 1;
                v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
            }

            for (int j = 0; j < v0.Length; j++)
                v0[j] = v1[j];
        }

        return v1[t.Length];
    }
}
