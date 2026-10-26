using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ContextBar.Pack.Pdf;

/// <summary>One output page: a page of one of the source PDFs plus extra clockwise rotation.</summary>
/// <param name="Source">Zero-based index of the source PDF in processing order.</param>
/// <param name="Page">Zero-based page index in that PDF.</param>
/// <param name="Rotation">Extra rotation in degrees: 0, 90, 180 or 270.</param>
public readonly record struct ArrangedPage(int Source, int Page, int Rotation);

/// <summary>
/// The page list produced by the visual page organizer and consumed by
/// <see cref="OrganizePagesPdfAction"/>. Text form: comma-separated "file:page" or "file:page@degrees",
/// one-based, for example "1:3, 1:1@90, 2:2". Parsing is bounded and never expands ranges.
/// </summary>
public static class PageArrangement
{
    /// <summary>Upper bound for output pages; protects the palette from huge parameter values.</summary>
    public const int MaxPages = 10_000;

    public static string Format(IEnumerable<ArrangedPage> pages)
    {
        var builder = new StringBuilder();
        foreach (var page in pages)
        {
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(CultureInfo.InvariantCulture, $"{page.Source + 1}:{page.Page + 1}");
            int rotation = Normalize(page.Rotation);
            if (rotation != 0) builder.Append(CultureInfo.InvariantCulture, $"@{rotation}");
        }
        return builder.ToString();
    }

    /// <summary>Parses the text form. Returns false with a user-facing error for invalid input.</summary>
    public static bool TryParse(string? text, out IReadOnlyList<ArrangedPage> pages, out string? error)
    {
        pages = Array.Empty<ArrangedPage>();
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = SR.GetString("OrganizePages_Error_Empty");
            return false;
        }

        var result = new List<ArrangedPage>();
        foreach (var raw in text.Split(','))
        {
            if (result.Count >= MaxPages)
            {
                error = string.Format(CultureInfo.CurrentCulture, SR.GetString("OrganizePages_Error_TooMany"), MaxPages);
                return false;
            }
            string entry = raw.Trim();
            int at = entry.IndexOf('@');
            string location = at < 0 ? entry : entry[..at];
            int colon = location.IndexOf(':');
            if (colon <= 0
                || !TryPositive(location[..colon], out int source)
                || !TryPositive(location[(colon + 1)..], out int page))
            {
                error = SR.GetString("OrganizePages_Error_Syntax");
                return false;
            }
            int rotation = 0;
            if (at >= 0 && (!int.TryParse(entry[(at + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out rotation)
                            || rotation % 90 != 0))
            {
                error = SR.GetString("OrganizePages_Error_Rotation");
                return false;
            }
            result.Add(new ArrangedPage(source - 1, page - 1, Normalize(rotation)));
        }
        pages = result;
        return true;
    }

    /// <summary>Checks file and page indices against the actual sources; returns an error or null.</summary>
    public static string? Validate(IReadOnlyList<ArrangedPage> pages, IReadOnlyList<int> pageCounts)
    {
        foreach (var page in pages)
        {
            if (page.Source >= pageCounts.Count)
                return SR.GetString("OrganizePages_Error_UnknownFile");
            if (page.Page >= pageCounts[page.Source])
                return SR.GetString("OrganizePages_Error_UnknownPage");
        }
        return null;
    }

    public static int Normalize(int degrees) => ((degrees % 360) + 360) % 360;

    private static bool TryPositive(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= 1_000_000;
}
