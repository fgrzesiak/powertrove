using System.Text;

namespace FileKit.Pdf;

/// <summary>An outline (bookmark) entry; <c>Top</c> is the top of its destination view in PDF units, when the destination names one.</summary>
public sealed record PdfOutlineItem(string Title, int? PageIndex, IReadOnlyList<PdfOutlineItem> Children, double? Top = null);

public static class PdfOutline
{
    public static IReadOnlyList<PdfOutlineItem> Read(PdfDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var outlines = document.ResolveDictionary(document.Catalog["Outlines"]);
        if (outlines is null) return Array.Empty<PdfOutlineItem>();

        var first = document.ResolveDictionary(outlines["First"]);
        if (first is null) return Array.Empty<PdfOutlineItem>();

        var state = new ReaderState(document);
        try
        {
            var result = ReadLevel(state, outlines["First"], 0, cancellationToken);
            return result;
        }
        catch (PdfException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new PdfOperationException("Failed to read the document outline.");
        }
    }

    private static IReadOnlyList<PdfOutlineItem> ReadLevel(ReaderState state, PdfObject? firstObj, int depth, CancellationToken cancellationToken)
    {
        if (depth > 50) return Array.Empty<PdfOutlineItem>();

        var items = new List<PdfOutlineItem>();
        var currentObj = firstObj;

        while (currentObj != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = state.Document.ResolveDictionary(currentObj);
            if (current == null) break;

            if (currentObj is PdfReference refObj)
            {
                if (!state.VisitedNodes.Add(refObj.ObjectNumber))
                {
                    break; // cycle detected
                }
            }
            else
            {
                // Unlikely for outline nodes to be inline, but if they are, limit by reference equality
                if (!state.VisitedDicts.Add(current))
                {
                    break;
                }
            }

            state.ItemCount++;
            if (state.ItemCount > 10000)
            {
                break; // limit total items
            }

            string title = ExtractTitle(state.Document.Resolve(current["Title"]));
            var target = state.Destinations.OfItem(current);

            IReadOnlyList<PdfOutlineItem> children = Array.Empty<PdfOutlineItem>();
            var firstChildObj = current["First"];
            if (firstChildObj != null)
            {
                children = ReadLevel(state, firstChildObj, depth + 1, cancellationToken);
            }

            items.Add(new PdfOutlineItem(title, target?.PageIndex, children, target?.Top));

            currentObj = current["Next"];
        }

        return items;
    }

    private static string ExtractTitle(PdfObject? titleObj)
    {
        if (titleObj is PdfString str)
        {
            var bytes = str.Value;
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                // UTF-16BE
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }
            else
            {
                // PDFDocEncoding / Latin-1 fallback
                return Encoding.GetEncoding("iso-8859-1").GetString(bytes);
            }
        }
        return string.Empty;
    }

    private sealed class ReaderState
    {
        public PdfDocument Document { get; }
        public HashSet<int> VisitedNodes { get; } = new();
        public HashSet<PdfDictionary> VisitedDicts { get; } = new(ReferenceEqualityComparer.Instance);
        public int ItemCount;

        public PdfDestinations Destinations { get; }

        public ReaderState(PdfDocument document)
        {
            Document = document;
            Destinations = new PdfDestinations(document);
        }
    }
    public static IReadOnlyList<PdfOutlineItem> Remap(IReadOnlyList<PdfOutlineItem> items, Func<int, int?> newIndex)
    {
        var result = new List<PdfOutlineItem>();
        foreach (var item in items)
        {
            var remappedChildren = Remap(item.Children, newIndex);

            if (!item.PageIndex.HasValue)
            {
                // Items without a destination are kept if they still have meaning (or just unconditionally, to preserve structure)
                result.Add(new PdfOutlineItem(item.Title, null, remappedChildren, item.Top));
            }
            else
            {
                int? mappedIndex = newIndex(item.PageIndex.Value);
                if (mappedIndex.HasValue)
                {
                    result.Add(new PdfOutlineItem(item.Title, mappedIndex, remappedChildren, item.Top));
                }
                else
                {
                    // Drop this item, but promote its surviving children to the same level
                    result.AddRange(remappedChildren);
                }
            }
        }
        return result;
    }
}
