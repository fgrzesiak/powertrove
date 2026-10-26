using PowerTrove.Application.Resources;
using PowerTrove.Application.ViewModels;
using PowerTrove.Core.Search;

namespace PowerTrove.Application.Search;

/// <summary>Presentation taxonomy for the current catalog; unknown actions always retain a fallback group.</summary>
internal static class ActionGrouping
{
    public static IReadOnlyList<ActionGroup> Create(IReadOnlyList<ActionSearchResult> results, bool searching, bool includeUnavailable)
    {
        var available = results.Where(r => r.IsCompatible).ToList();
        var groups = new List<ActionGroup>();
        if (searching)
        {
            // A query expresses intent: preserve the engine's global text-match order.
            if (available.Count > 0) groups.Add(new ActionGroup(string.Empty, available));
        }
        else
        {
            var suggested = available.Where(r => r.Highlight != ActionHighlight.None).Take(3).ToList();
            if (suggested.Count > 0) groups.Add(new ActionGroup(Strings.Group_Suggested, suggested));
            var ids = suggested.Select(r => r.Action.Descriptor.Id).ToHashSet(StringComparer.Ordinal);
            // GroupBy preserves first occurrence: the strongest remaining group comes first.
            foreach (var group in available.Where(r => !ids.Contains(r.Action.Descriptor.Id)).GroupBy(r => Key(r.Action.Descriptor.Id)))
                groups.Add(new ActionGroup(Title(group.Key), group.ToArray()));
        }
        if (includeUnavailable)
        {
            var unavailable = results.Where(r => !r.IsCompatible).ToArray();
            if (unavailable.Length > 0) groups.Add(new ActionGroup(Strings.GetString("Group_Unavailable"), unavailable));
        }
        return groups;
    }

    private static string Key(string id)
    {
        string[] parts = id.Split('.');
        if (parts.Length < 3 || parts[0] != "contextbar") return "All";
        string family = parts[1], op = parts[2];
        if (family == "general") return "General";
        if (family == "recipe") return "Workflows";
        string category = op switch
        {
            "remove-metadata" or "remove-location" or "blur-faces" or "protect" or "unlock" or "sanitize" or "remove-hidden" => "Privacy",
            "info" or "to-text" or "ocr" or "extract-text" or "extract-images" or "count" or "checksums" or "find-duplicates" => "Inspect",
            "compress" or "optimize-png" or "optimize" => "Optimize",
            "merge" or "combine" or "from-images" or "images-to-pdf" or "stitch" or "collage" or "to-gif" or "join" => "Combine",
            "reorder" or "remove-pages" or "extract-pages" or "organize-pages" or "split" or "split-every" or "split-by-size" => "Pages",
            "resize" or "crop-aspect" or "pad" or "target-size" => "Layout",
            "auto-rotate" or "rotate-right" or "rotate-left" or "rotate-180" or "flip-horizontal" or "flip-vertical" => "Orientation",
            _ when op.StartsWith("convert-", StringComparison.Ordinal) || op.StartsWith("export-", StringComparison.Ordinal) || op.StartsWith("to-", StringComparison.Ordinal) || op.StartsWith("preset-", StringComparison.Ordinal) => "Convert",
            _ => "Edit"
        };
        return family switch
        {
            "image" => "Image_" + (category == "Pages" ? "Layout" : category),
            "pdf" => "Pdf_" + (category is "Layout" or "Orientation" ? "Pages" : category),
            "audio" or "video" => "Media_" + (category is "Layout" or "Orientation" or "Pages" ? "Edit" : category),
            "files" => op.Contains("zip", StringComparison.Ordinal) || op.Contains("tar", StringComparison.Ordinal) || op.StartsWith("extract", StringComparison.Ordinal) ? "Archives" : "Files",
            "data" or "table" => "Data",
            _ => "All"
        };
    }

    private static string Title(string key) => Strings.GetString("Group_" + key);
}
