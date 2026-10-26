using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Core.Search;

public class ActionSearchEngine : IActionSearchEngine
{
    public void RefreshSignals()
    {
        _usageSignals?.Refresh();
    }

    private readonly IActionRegistry _registry;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>>? _actionAliases;
    private readonly IReadOnlySet<string>? _pinnedActions;
    private readonly IReadOnlyDictionary<string, int>? _recentUsageScores;
    private readonly IActionUsageSignals? _usageSignals;

    public ActionSearchEngine(
        IActionRegistry registry,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? actionAliases = null,
        IReadOnlySet<string>? pinnedActions = null,
        IReadOnlyDictionary<string, int>? recentUsageScores = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _actionAliases = actionAliases;
        _pinnedActions = pinnedActions;
        _recentUsageScores = recentUsageScores;
    }

    public ActionSearchEngine(
        IActionRegistry registry,
        IActionUsageSignals usageSignals,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? actionAliases = null,
        IReadOnlySet<string>? pinnedActions = null,
        IReadOnlyDictionary<string, int>? recentUsageScores = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _usageSignals = usageSignals ?? throw new ArgumentNullException(nameof(usageSignals));
        _actionAliases = actionAliases;
        _pinnedActions = pinnedActions;
        _recentUsageScores = recentUsageScores;
    }

    public async Task<IReadOnlyList<ActionSearchResult>> SearchAsync(
        string query,
        ContextSnapshot context,
        CancellationToken cancellationToken = default)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));

        var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

        var signature = PowerTrove.Core.Selection.SelectionSignature.From(context);
        string signatureKey = signature.Key;
        string? lastUsedId = _usageSignals?.LastUsedAction(signatureKey);

        var actions = _registry.GetAllActions();

        var tasks = actions.Select(async action =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var staticAvailability = ActionContextCompatibility.Check(context, action.Descriptor);
            bool isCompatible = staticAvailability.IsAvailable;
            string? incompatibilityReason = staticAvailability.Reason;

            string? availabilityReason = null;
            if (isCompatible)
            {
                try
                {
                    // Actions may reject a context dynamically. Keep search usable
                    // even when an individual action's matcher throws.
                    var availability = await action.MatchAsync(context, cancellationToken).ConfigureAwait(false);
                    isCompatible = availability.IsAvailable;
                    if (!isCompatible)
                        incompatibilityReason = availability.Reason ?? PowerTrove.Core.Resources.Strings.ActionNotAvailable;
                    else
                        availabilityReason = availability.Reason;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    isCompatible = false;
                    incompatibilityReason = PowerTrove.Core.Resources.Strings.ActionAvailabilityUnknown;
                }
            }

            int rank = CalculateRank(action, query, isCompatible, context, signatureKey, lastUsedId, out var fit, out var highlight, out int textMatchScore);

            // Partial fits (where dynamic MatchAsync returned a reason but is still compatible)
            // must never be Ideal, and should lose the Ideal rank bonus.
            if (action is not CatalogAction && isCompatible && fit == PowerTrove.Abstractions.SelectionFit.Ideal && !string.IsNullOrEmpty(availabilityReason))
            {
                fit = PowerTrove.Abstractions.SelectionFit.Acceptable;
                rank -= 140; // Remove Ideal bonus (200) and add Acceptable bonus (60)
            }

            // If there's a query and rank is low (no real match), we skip unless it's a very broad search
            if (!string.IsNullOrWhiteSpace(query) && rank < 10)
            {
                return null;
            }

            // Apply after query filtering so the discovery penalty never hides a weak text match.
            if (action is CatalogAction && isCompatible) rank -= 150;

            return new ActionSearchResult(action, isCompatible, incompatibilityReason, rank, highlight, fit, availabilityReason, textMatchScore,
                action is CatalogAction ? PackInstallState.NotInstalled : PackInstallState.Installed);
        });

        var taskResults = await Task.WhenAll(tasks).ConfigureAwait(false);
        var results = taskResults.Where(r => r != null).Select(r => r!).ToList();

        // Best fit assignment logic
        var bestFitAction = results
            .Where(r => r.InstallState != PackInstallState.NotInstalled && r.IsCompatible && r.Fit == PowerTrove.Abstractions.SelectionFit.Ideal && r.Highlight == ActionHighlight.None)
            .OrderByDescending(r => string.IsNullOrWhiteSpace(query) ? 0 : r.TextMatchScore)
            .ThenByDescending(r => r.Rank)
            .ThenByDescending(r => string.IsNullOrWhiteSpace(query) ? BrowsePriority.For(r.Action.Descriptor, signature) : 0)
            .ThenBy(r => r.Action.Descriptor.Title)
            .ThenBy(r => r.Action.Descriptor.Id)
            .FirstOrDefault();

        if (bestFitAction != null && !results.Any(r => r.IsCompatible && r.Highlight == ActionHighlight.LastUsedForSelection))
        {
            int index = results.IndexOf(bestFitAction);
            results[index] = bestFitAction with { Highlight = ActionHighlight.BestFit };
        }

        // Compatibility is a hard boundary. While typing, text tiers precede context/history;
        // otherwise local usage and fit lead, with common tasks breaking equal-rank ties.
        bool hasQuery = !string.IsNullOrWhiteSpace(query);
        var sorted = results
            .OrderByDescending(r => r.IsCompatible)
            .ThenByDescending(r => hasQuery ? r.TextMatchScore : 0)
            .ThenByDescending(r => r.Rank)
            .ThenByDescending(r => hasQuery ? 0 : BrowsePriority.For(r.Action.Descriptor, signature))
            .ThenBy(r => r.Action.Descriptor.Title)
            .ThenBy(r => r.Action.Descriptor.Id)
            .ToList();

        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        PowerTrove.Core.Diagnostics.InvocationMetrics.SearchLatency.Record(elapsedMs);

        return sorted;
    }

    private int CalculateRank(IAction action, string query, bool isCompatible, ContextSnapshot context, string signatureKey, string? lastUsedId, out PowerTrove.Abstractions.SelectionFit fit, out ActionHighlight highlight, out int textMatchScore)
    {
        int rank = 0;
        fit = PowerTrove.Abstractions.SelectionFit.None;
        highlight = ActionHighlight.None;
        textMatchScore = 0;

        // Base score for compatibility
        if (isCompatible)
        {
            rank += 1000;

            if (action.Descriptor.Selection != null)
            {
                fit = PowerTrove.Abstractions.SelectionRequirements.Evaluate(action.Descriptor.Selection, context);
                if (fit == PowerTrove.Abstractions.SelectionFit.Ideal) rank += 200;
                else if (fit == PowerTrove.Abstractions.SelectionFit.Acceptable) rank += 60;
            }

            bool hasQuery = !string.IsNullOrWhiteSpace(query);

            int usagePoints = 0;

            if (lastUsedId == action.Descriptor.Id)
            {
                usagePoints += 300;
                highlight = ActionHighlight.LastUsedForSelection;
            }

            if (_pinnedActions != null && _pinnedActions.Contains(action.Descriptor.Id))
            {
                usagePoints += 100;
                if (highlight == ActionHighlight.None) highlight = ActionHighlight.Favorite;
            }

            if (_usageSignals != null)
            {
                double s = _usageSignals.SelectionScore(signatureKey, action.Descriptor.Id);
                if (s > 0)
                {
                    usagePoints += Math.Min(150, (int)Math.Round(40 * Math.Log(1 + s)));
                }
            }

            if (_recentUsageScores != null && _recentUsageScores.TryGetValue(action.Descriptor.Id, out int usageScore))
            {
                usagePoints += Math.Min(40, usageScore);
            }

            if (hasQuery)
            {
                usagePoints /= 2;
                usagePoints = Math.Min(9, usagePoints); // S4 rule: max 9 points for usage when there is a query, so a strong text match (+10 tiers) outranks
            }

            rank += usagePoints;
        }

        // Context Specificity: Boost if the action explicitly accepts the current content kind (and not just None/fallback)
        ContentKind kind = context.Content switch
        {
            TableData => ContentKind.Table,
            FileSelection => ContentKind.Files,
            TextSelection => ContentKind.Text,
            _ => ContentKind.None
        };

        if (kind != ContentKind.None && action.Descriptor.Accepts.Contains(kind))
        {
            rank += 150;
        }

        // App Identity: Boost actions that are designed for the current foreground app
        if (context.Source.ProcessName != null && action.Descriptor.SearchTerms.Contains(context.Source.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            rank += 125;
        }

        // Explicit Weight
        rank += action.Descriptor.ExplicitWeight;

        // If no query, we just return the compatibility base score plus contextual boosts
        if (string.IsNullOrWhiteSpace(query))
        {
            return rank;
        }

        // Text matching score (0-100)
        int matchScore = 0;

        // Match against title
        matchScore = Math.Max(matchScore, FuzzyMatcher.CalculateScore(query, action.Descriptor.Title));

        // Match against search terms / aliases
        if (action.Descriptor.SearchTerms != null)
        {
            foreach (var term in action.Descriptor.SearchTerms)
            {
                matchScore = Math.Max(matchScore, FuzzyMatcher.CalculateScore(query, term));
            }
        }

        // Match against description (lower weight)
        if (!string.IsNullOrWhiteSpace(action.Descriptor.Description))
        {
            int descScore = FuzzyMatcher.CalculateScore(query, action.Descriptor.Description);
            matchScore = Math.Max(matchScore, descScore / 2);
        }

        // Match against configured aliases
        if (_actionAliases != null && _actionAliases.TryGetValue(action.Descriptor.Id, out var aliases))
        {
            if (aliases != null)
            {
                foreach (var alias in aliases)
                {
                    matchScore = Math.Max(matchScore, FuzzyMatcher.CalculateScore(query, alias));
                }
            }
        }

        // If it didn't match the query well at all, remove compatibility score to effectively filter it out
        if (matchScore == 0)
        {
            return 0;
        }

        // Stable Reordering: Bucket match scores into tiers of 10 to prevent unstable reordering during live typing
        int bucketedMatchScore = (matchScore / 10) * 10;
        if (matchScore == 100) bucketedMatchScore = 100;
        // An exact visible title is stronger than an exact keyword/word match inside a longer title.
        textMatchScore = bucketedMatchScore + (string.Equals(query.Trim(), action.Descriptor.Title.Trim(), StringComparison.OrdinalIgnoreCase) ? 10 : 0);

        rank += bucketedMatchScore;

        return rank;
    }
}
