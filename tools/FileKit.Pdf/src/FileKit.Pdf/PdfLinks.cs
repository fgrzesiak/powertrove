namespace FileKit.Pdf;

/// <summary>
/// A link annotation: its rectangle and either a web or mail address (<see cref="Uri"/>) or a place in the document.
/// Only <c>http</c>, <c>https</c> and <c>mailto</c> addresses are kept; other schemes (files, scripts) are dropped.
/// </summary>
public sealed record PdfLayoutLink(double Left, double Bottom, double Right, double Top, string? Uri, PdfDestination? Target);

internal static class PdfLinks
{
    private const int MaximumLinksPerPage = 2000;

    public static IReadOnlyList<PdfLayoutLink> Read(PdfDocument document, PdfPage page, PdfDestinations destinations, CancellationToken ct)
    {
        if (document.Resolve(page.Dictionary["Annots"]) is not PdfArray annotations) return [];
        var links = new List<PdfLayoutLink>();
        foreach (var item in annotations.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (links.Count >= MaximumLinksPerPage) break;
            if (document.ResolveDictionary(item) is not { } annotation || annotation.GetName("Subtype") != "Link") continue;
            if (document.Resolve(annotation["Rect"]) is not PdfArray { Count: 4 } rect) continue;
            double[] r = rect.Items.Select(v => PdfLayoutReader.Number(document.Resolve(v))).ToArray();
            string? uri = null;
            PdfDestination? target = null;
            if (document.ResolveDictionary(annotation["A"]) is { } action && action.GetName("S") == "URI")
                uri = Address(document.Resolve(action["URI"]));
            else target = destinations.OfItem(annotation);
            if (uri is null && target is null) continue;
            links.Add(new PdfLayoutLink(Math.Min(r[0], r[2]), Math.Min(r[1], r[3]), Math.Max(r[0], r[2]), Math.Max(r[1], r[3]), uri, target));
        }
        return links;
    }

    private static string? Address(PdfObject? value)
    {
        if (value is not PdfString text) return null;
        string address = PdfDestinations.Text(text).Trim();
        if (address.Length is 0 or > 2048 || address.Any(char.IsControl)) return null;
        return Uri.TryCreate(address, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" or "mailto" ? address : null;
    }
}
