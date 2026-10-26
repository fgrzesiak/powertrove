using PowerTrove.Core.Selection;

namespace PowerTrove.Core.Search;

/// <summary>Deterministic tie-breaks only; compatibility, fit, pins and learned usage keep their scores.</summary>
internal static class BrowsePriority
{
    public static int For(ActionDescriptor descriptor, SelectionSignature selection)
    {
        int specificity = descriptor.Selection?.Families.Count > 0 ? 100 : 0;
        if (!descriptor.Id.StartsWith("powertrove.", StringComparison.Ordinal)) return specificity;
        string operation = descriptor.Id[(descriptor.Id.LastIndexOf('.') + 1)..];
        // A stamp has a narrower two-family contract than a general PDF combination.
        if (operation == "stamp-selected-image") return specificity + 95;
        // Multi-input actions are already gated by matching/fit before this tie-break is used.
        if (selection.Count != CountBucket.One && descriptor.Selection is { } requirements
            && Math.Max(requirements.MinItems, requirements.IdealMinItems ?? 1) >= 2)
            return specificity + (operation is "merge" or "combine" or "from-images" or "images-to-pdf" ? 90 : 80);
        int commonTask = operation switch
        {
            "convert-webp" or "merge" or "combine" or "to-pdf" => 60,
            "resize" or "compress" or "zip" => 55,
            "convert-jpeg" or "convert-mp4" or "export-csv" => 50,
            "extract-pages" => 65,
            "convert-png" or "extract" => 45,
            "convert-avif" or "export-json" => 40,
            "info" or "count" => 5,
            _ => 20
        };
        return specificity + commonTask;
    }
}
