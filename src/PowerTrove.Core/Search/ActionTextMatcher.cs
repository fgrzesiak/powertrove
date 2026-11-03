using System;
using System.Collections.Generic;
using System.Linq;
using PowerTrove.Abstractions;

namespace PowerTrove.Core.Search;

/// <summary>
/// Query relevance is determined entirely from public action metadata before context-dependent
/// MatchAsync calls. Exact titles always outrank synonyms, and a user may combine words
/// from the title, pack metadata and aliases without matching a long description by accident.
/// </summary>
internal static class ActionTextMatcher
{
    public static int Score(string query, ActionDescriptor descriptor, IReadOnlyList<string>? aliases)
    {
        // A bounded fuzzy query prevents pathological work, but an exact long title must
        // still be discoverable (the manifest permits titles longer than the fuzzy budget).
        if (query.Length > 256)
            return string.Equals(query.Trim(), descriptor.Title.Trim(), StringComparison.OrdinalIgnoreCase) ? 110 : 0;
        string normalized = FuzzyMatcher.Normalize(query);
        if (normalized.Length == 0) return 0;
        string title = descriptor.Title;
        string normalizedTitle = FuzzyMatcher.Normalize(title);
        if (normalized == normalizedTitle) return 110;

        int titleScore = FuzzyMatcher.CalculateScore(query, title);
        if (titleScore > 0 && normalizedTitle.StartsWith(normalized, StringComparison.Ordinal))
            titleScore = 100;
        else if (titleScore == 100) titleScore = 98;
        if (titleScore >= 95) return titleScore; // Nothing in another field can beat a strong title.

        var terms = descriptor.SearchTerms ?? Array.Empty<string>();
        int termScore = terms.Select(term => Adjust(FuzzyMatcher.CalculateScore(query, term), 5)).DefaultIfEmpty(0).Max();
        int aliasScore = (aliases ?? Array.Empty<string>()).Select(alias => Adjust(FuzzyMatcher.CalculateScore(query, alias), 5)).DefaultIfEmpty(0).Max();

        int idScore = Adjust(FuzzyMatcher.CalculateScore(query, descriptor.Id), 12);
        int score = Math.Max(Math.Max(titleScore, termScore), Math.Max(aliasScore, idScore));
        if (score >= 95) return score; // Exact alias or keyword: descriptions cannot outrank it.

        // Permit "convert image webp" when the words belong to different search fields.
        if (normalized.Contains(' '))
        {
            string combined = string.Join(" ", new[] { title }.Concat(terms).Concat(aliases ?? Array.Empty<string>()));
            score = Math.Max(score, Adjust(FuzzyMatcher.CalculateScore(query, combined), 15));
            // Descriptions are discovery fallback only, not equivalent to exact user-visible names.
            if (!string.IsNullOrWhiteSpace(descriptor.Description))
                score = Math.Max(score, Adjust(FuzzyMatcher.CalculateScore(query, combined + " " + descriptor.Description), 45));
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Description))
            score = Math.Max(score, FuzzyMatcher.CalculateScore(query, descriptor.Description) / 2);
        return score;
    }

    private static int Adjust(int score, int penalty) => score == 0 ? 0 : Math.Max(1, score - penalty);
}
