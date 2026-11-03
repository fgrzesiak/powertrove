using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions.Files;
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
        bool searching = !string.IsNullOrWhiteSpace(query);

        var tasks = actions.Select(async action =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string>? aliases = null;
            if (_actionAliases is not null) _actionAliases.TryGetValue(action.Descriptor.Id, out aliases);
            int textMatchScore = searching ? ActionTextMatcher.Score(query, action.Descriptor, aliases) : 0;
            if (searching && textMatchScore == 0) return null; // Do not invoke potentially slow pack matchers for unrelated actions.

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

            int rank = CalculateRank(action, query, isCompatible, context, signatureKey, lastUsedId, textMatchScore, out var fit, out var highlight);

            // Partial fits (where dynamic MatchAsync returned a reason but is still compatible)
            // must never be Ideal, and should lose the Ideal rank bonus.
            if (action is not CatalogAction && isCompatible && fit == PowerTrove.Abstractions.SelectionFit.Ideal && !string.IsNullOrEmpty(availabilityReason))
            {
                fit = PowerTrove.Abstractions.SelectionFit.Acceptable;
                rank -= 140; // Remove Ideal bonus (200) and add Acceptable bonus (60)
            }

            // Apply after query filtering so the discovery penalty never hides a weak text match.
            if (action is CatalogAction && isCompatible) rank -= 150;

            return new ActionSearchResult(action, isCompatible, incompatibilityReason, rank, highlight, fit, availabilityReason, textMatchScore,
                action is CatalogAction catalog ? catalog.InstallState : PackInstallState.Installed);
        });

        var taskResults = await Task.WhenAll(tasks).ConfigureAwait(false);
        var results = taskResults.Where(r => r != null).Select(r => r!).ToList();

        // Best fit assignment logic
        var bestFitAction = results
            .Where(r => r.Action is not CatalogAction && r.IsCompatible && r.Fit == PowerTrove.Abstractions.SelectionFit.Ideal && r.Highlight == ActionHighlight.None)
            .OrderByDescending(r => searching ? TextTier(r.TextMatchScore) : 0)
            .ThenByDescending(r => IsTypeSpecific(r))
            .ThenByDescending(r => searching ? TitleCoverage(query, r.Action.Descriptor.Title) : 0)
            .ThenByDescending(r => r.Rank)
            .ThenByDescending(r => string.IsNullOrWhiteSpace(query) ? BrowsePriority.For(r.Action.Descriptor, signature) : 0)
            .ThenBy(r => searching ? 0 : Narrowness(r))
            .ThenBy(r => r.Action.Descriptor.Title)
            .ThenBy(r => r.Action.Descriptor.Id)
            .FirstOrDefault();

        if (bestFitAction != null && !results.Any(r => r.IsCompatible && r.Highlight == ActionHighlight.LastUsedForSelection))
        {
            int index = results.IndexOf(bestFitAction);
            results[index] = bestFitAction with { Highlight = ActionHighlight.BestFit };
        }

        // Compatibility is a hard boundary. While typing, text tiers lead; within a tier, actions made for the
        // selected file type beat generic file actions, and titles the query covers more fully come first,
        // before context and history. Without a query, tiers lead: the action last used for this kind of selection
        // and pinned actions, then actions made for the selected file types, then general file actions, then actions
        // of packs that are not installed. Within a tier, fit and local usage lead, then common tasks, then the narrower
        // input set. Global recency can therefore never lift "Compress to ZIP" above a Markdown viewer.
        bool hasQuery = searching;
        var sorted = results
            .OrderByDescending(r => r.IsCompatible)
            .ThenByDescending(r => hasQuery ? TextTier(r.TextMatchScore) : BrowseTier(r))
            .ThenByDescending(r => hasQuery && IsTypeSpecific(r))
            .ThenByDescending(r => hasQuery ? TitleCoverage(query, r.Action.Descriptor.Title) : 0)
            .ThenByDescending(r => r.Rank)
            .ThenByDescending(r => hasQuery ? 0 : BrowsePriority.For(r.Action.Descriptor, signature))
            .ThenBy(r => hasQuery ? 0 : Narrowness(r))
            .ThenBy(r => r.Action.Descriptor.Title)
            .ThenBy(r => r.Action.Descriptor.Id)
            .ToList();

        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        PowerTrove.Core.Diagnostics.InvocationMetrics.SearchLatency.Record(elapsedMs);

        return sorted;
    }

    /// <summary>
    /// An action made for the selected kinds of files: it names file families (images, PDFs, folders) or a finite set
    /// of extensions, and it applies to the whole selection. Actions for any file (ZIP, rename) and actions that only
    /// handle part of a mixed selection are general file actions.
    /// </summary>
    private static bool IsTypeSpecific(ActionSearchResult result) =>
        result.IsCompatible && result.Action is not CatalogAction && result.Fit == PowerTrove.Abstractions.SelectionFit.Ideal
        && (result.Action.Descriptor.Selection is { Families.Count: > 0 } || result.Action is IFileActionMetadata { InputExtensions.Count: > 0 });

    /// <summary>
    /// Strong matches (exact title 110, title prefix 100, exact word 95+) stay distinct; weaker fuzzy
    /// scores share tiers of ten, so the selected file type decides instead of a one-point difference,
    /// and the order does not jump while the user types.
    /// </summary>
    private static int TextTier(int score) => score >= 95 ? score : score / 10 * 10;

    private static int BrowseTier(ActionSearchResult result) =>
        result.Action is CatalogAction ? 0
        : result.Highlight is ActionHighlight.LastUsedForSelection or ActionHighlight.Favorite ? 3
        : IsTypeSpecific(result) ? 2 : 1;

    /// <summary>Accepted extensions: a Markdown viewer (5) is more specific than a text converter for 16 formats.</summary>
    private static int Narrowness(ActionSearchResult result) =>
        result.Action is IFileActionMetadata { InputExtensions.Count: > 0 } metadata ? metadata.InputExtensions.Count : int.MaxValue;

    /// <summary>The share of the title's words that the query matches: "Compress Images" before "Compress to File Size".</summary>
    private static double TitleCoverage(string query, string title)
    {
        var titleWords = FuzzyMatcher.Normalize(title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (titleWords.Length == 0) return 0;
        var queryWords = FuzzyMatcher.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int matched = titleWords.Count(t => queryWords.Any(q => FuzzyMatcher.CalculateScore(q, t) >= 90));
        return (double)matched / titleWords.Length;
    }

    private int CalculateRank(IAction action, string query, bool isCompatible, ContextSnapshot context, string signatureKey, string? lastUsedId, int textMatchScore, out PowerTrove.Abstractions.SelectionFit fit, out ActionHighlight highlight)
    {
        int rank = 0;
        fit = PowerTrove.Abstractions.SelectionFit.None;
        highlight = ActionHighlight.None;
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

        // Browser-specific suggestions are small default boosts, never compatibility
        // overrides. A user's existing last-used/history weights remain stronger.
        if (isCompatible && context.BrowserPage is { } page && string.IsNullOrWhiteSpace(query))
        {
            string id = action.Descriptor.Id;
            if (page.HasSelection && id.StartsWith("powertrove.text.", StringComparison.Ordinal))
                rank += 65;
            if (id.StartsWith("powertrove.browser.", StringComparison.Ordinal))
                rank += page.HasSelection ? 50 : 80;
            if (page.IsArticle && id.Contains("markdown", StringComparison.OrdinalIgnoreCase))
                rank += 30;
            if (page.TableCount > 0 && id.StartsWith("powertrove.tables.", StringComparison.Ordinal))
                rank += 40;
        }

        // Explicit Weight
        rank += action.Descriptor.ExplicitWeight;

        // If no query, we just return the compatibility base score plus contextual boosts
        if (string.IsNullOrWhiteSpace(query))
        {
            return rank;
        }

        // Rank text only after compatibility and contextual boosts; TextMatchScore separately
        // determines relevance tiers before history, context or explicit weights.
        rank += textMatchScore;
        return rank;
    }
}
