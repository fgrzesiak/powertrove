using System;
using System.Collections.Generic;
using System.Linq;

namespace ContextBar.Abstractions;

public enum SelectionFit { None, Acceptable, Ideal }

public sealed record FamilyLimit(int Min, int? Max);

public sealed record SelectionRequirements(
    IReadOnlySet<string> Families,
    int MinItems = 1,
    int? MaxItems = null,
    bool AllowOtherItems = true,
    int? IdealMinItems = null,
    int? IdealMaxItems = null,
    bool IdealNeedsEveryFamily = false,
    IReadOnlyDictionary<string, FamilyLimit>? FamilyLimits = null,
    int? IdealMinDistinctFamilies = null)
{
    // IdealNeedsEveryFamily: the fit is Ideal only when each listed family is present, for
    // example "Combine into One PDF" with {pdf, image} is ideal for PDFs plus images but merely
    // acceptable for images alone (where "Create PDF from Images" fits better).
    public static SelectionRequirements Single(params string[] families)
    {
        var set = new HashSet<string>(families, StringComparer.OrdinalIgnoreCase);
        return new SelectionRequirements(set, MinItems: 1, MaxItems: 1, AllowOtherItems: false, IdealMinItems: 1, IdealMaxItems: 1);
    }

    public static SelectionRequirements Multiple(params string[] families)
    {
        var set = new HashSet<string>(families, StringComparer.OrdinalIgnoreCase);
        return new SelectionRequirements(set, MinItems: 2, IdealMinItems: 2);
    }

    public static SelectionRequirements Any(params string[] families)
    {
        var set = new HashSet<string>(families, StringComparer.OrdinalIgnoreCase);
        return new SelectionRequirements(set, MinItems: 1, IdealMinItems: 1);
    }

    public static SelectionFit Evaluate(SelectionRequirements r, ContextSnapshot s)
    {
        if (s.Status != ContextStatus.Available || s.Content is not FileSelection fs)
            return SelectionFit.None;

        int matchingCount = 0;
        int otherCount = 0;
        var presentFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in fs.Files)
        {
            string family = SelectionFamilies.Of(file);

            if (r.Families.Count == 0 || r.Families.Contains(family))
            {
                matchingCount++;
                presentFamilies.Add(family);
            }
            else
            {
                otherCount++;
            }
        }

        if (r.GetFamilyLimitProblem(fs) != null)
            return SelectionFit.None;

        if (matchingCount < r.MinItems || (r.MaxItems.HasValue && matchingCount > r.MaxItems.Value))
            return SelectionFit.None;

        if (!r.AllowOtherItems && otherCount > 0)
            return SelectionFit.None;

        bool idealCount = (!r.IdealMinItems.HasValue || matchingCount >= r.IdealMinItems.Value) &&
                          (!r.IdealMaxItems.HasValue || matchingCount <= r.IdealMaxItems.Value);

        bool idealFamilies = (!r.IdealNeedsEveryFamily || r.Families.All(presentFamilies.Contains)) &&
            (!r.IdealMinDistinctFamilies.HasValue || presentFamilies.Count >= r.IdealMinDistinctFamilies.Value);

        if (idealCount && idealFamilies && otherCount == 0)
            return SelectionFit.Ideal;

        return SelectionFit.Acceptable;
    }

    internal string? GetFamilyLimitProblem(FileSelection selection)
    {
        if (FamilyLimits is null || FamilyLimits.Count == 0) return null;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in selection.Files)
        {
            string family = SelectionFamilies.Of(file);
            counts[family] = counts.TryGetValue(family, out int count) ? count + 1 : 1;
        }

        foreach (var limit in FamilyLimits)
        {
            int count = counts.TryGetValue(limit.Key, out int value) ? value : 0;
            if (count < limit.Value.Min)
                return Resources.SdkStrings.RequiresMinItems(limit.Value.Min, limit.Key.ToUpperInvariant());
            if (limit.Value.Max is int max && count > max)
                return Resources.SdkStrings.RequiresMaxItems(max, limit.Key.ToUpperInvariant());
        }

        return null;
    }
}
