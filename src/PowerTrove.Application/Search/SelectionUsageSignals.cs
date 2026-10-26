using System;
using System.Collections.Generic;
using System.Linq;
using PowerTrove.Application.Configuration;
using PowerTrove.Core.Search;

namespace PowerTrove.Application.Search;

public sealed class SelectionUsageSignals : IActionUsageSignals
{
    private IReadOnlyDictionary<string, Dictionary<string, SelectionUsageEntry>> _usage;
    private readonly ISettingsService _settingsService;
    private readonly DateTimeOffset? _nowProvider;

    public SelectionUsageSignals(ISettingsService settingsService, DateTimeOffset? now = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _nowProvider = now;
        _usage = new Dictionary<string, Dictionary<string, SelectionUsageEntry>>();
        Refresh();
    }

    public void Refresh()
    {
        var settings = _settingsService.Settings;

        if (settings.SelectionUsage != null)
        {
            var copy = new Dictionary<string, Dictionary<string, SelectionUsageEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in settings.SelectionUsage)
            {
                var innerCopy = new Dictionary<string, SelectionUsageEntry>(kvp.Value, StringComparer.OrdinalIgnoreCase);
                copy[kvp.Key] = innerCopy;
            }
            _usage = copy;
        }
        else
        {
            _usage = new Dictionary<string, Dictionary<string, SelectionUsageEntry>>();
        }
    }

    public double SelectionScore(string signatureKey, string actionId)
    {
        var now = _nowProvider ?? DateTimeOffset.UtcNow;
        if (_usage.TryGetValue(signatureKey, out var actions) && actions.TryGetValue(actionId, out var entry))
        {
            double ageDays = (now - entry.LastUsedUtc).TotalDays;
            if (ageDays < 0) ageDays = 0;
            return entry.Count * Math.Pow(0.5, ageDays / 30.0);
        }
        return 0;
    }

    public string? LastUsedAction(string signatureKey)
    {
        if (_usage.TryGetValue(signatureKey, out var actions) && actions.Count > 0)
        {
            return actions.OrderByDescending(x => x.Value.LastUsedUtc).First().Key;
        }
        return null;
    }
}
