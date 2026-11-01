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
        bool several = selection.Count != CountBucket.One;
        // Multi-input actions are already gated by matching/fit before this tie-break is used.
        if (several && descriptor.Selection is { } requirements
            && Math.Max(requirements.MinItems, requirements.IdealMinItems ?? 1) >= 2)
            return specificity + operation switch
            {
                // Comparing takes exactly two images and is the most specific task for them (smart-combinations C13).
                "compare" => 92,
                "merge" or "merge-csv" or "merge-text" or "combine" or "from-images" or "images-to-pdf" => 90,
                // A collage is the usual way to put several images together; GIF and side by side follow it.
                "collage" => 84,
                _ => 80
            };
        // Several files of one kind are mostly processed in a batch: resizing and compressing follow the
        // combination that fits them (selection-intelligence S7: 2+ images, 2+ PDFs).
        if (several && selection.Families.Count == 1 && operation is "resize" or "compress")
            return specificity + 85;
        int commonTask = operation switch
        {
            // Formatting is what most people want from a single JSON file, not a re-encoding.
            "convert-webp" or "merge" or "combine" or "to-pdf" or "json-format" or "csv-to-xlsx" => 60,
            "resize" or "compress" or "zip" => 55,
            // Compressing is the most common task for a single PDF; extracting pages follows it.
            "convert-jpeg" or "convert-mp4" or "export-csv" or "extract-pages" or "trim" or "csv-to-json" or "json-to-csv" => 50,
            "convert-png" or "extract" or "json-validate" => 45,
            "convert-avif" or "export-json" => 40,
            "info" or "count" => 5,
            _ => 20
        };
        return specificity + commonTask;
    }
}
