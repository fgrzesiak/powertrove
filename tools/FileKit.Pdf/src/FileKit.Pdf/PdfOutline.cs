using System.Text;

namespace FileKit.Pdf;

public sealed record PdfOutlineItem(string Title, int? PageIndex, IReadOnlyList<PdfOutlineItem> Children);

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
            int? pageIndex = ResolveDest(state, current);

            IReadOnlyList<PdfOutlineItem> children = Array.Empty<PdfOutlineItem>();
            var firstChildObj = current["First"];
            if (firstChildObj != null)
            {
                children = ReadLevel(state, firstChildObj, depth + 1, cancellationToken);
            }

            items.Add(new PdfOutlineItem(title, pageIndex, children));

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

    private static int? ResolveDest(ReaderState state, PdfDictionary outlineItem)
    {
        var dest = state.Document.Resolve(outlineItem["Dest"]);
        if (dest != null)
        {
            return ResolveDestValue(state, dest);
        }

        var a = state.Document.ResolveDictionary(outlineItem["A"]);
        if (a != null)
        {
            var s = state.Document.Resolve(a["S"]) as PdfName;
            if (s != null && s.Value == "GoTo")
            {
                var d = state.Document.Resolve(a["D"]);
                if (d != null)
                {
                    return ResolveDestValue(state, d);
                }
            }
        }

        return null;
    }

    private static int? ResolveDestValue(ReaderState state, PdfObject dest)
    {
        if (dest is PdfArray array)
        {
            if (array.Count > 0 && array[0] is PdfReference pr)
            {
                var pageDict = state.Document.ResolveDictionary(pr);
                if (pageDict != null && pageDict.GetName("Type") == "Page")
                {
                    for (int i = 0; i < state.Document.PageCount; i++)
                    {
                        if (state.Document.Pages[i].Dictionary == pageDict)
                        {
                            return i;
                        }
                    }
                }
            }
            else if (array.Count > 0 && array[0] is PdfInteger pi)
            {
                int p = (int)pi.Value;
                if (p >= 0 && p < state.Document.PageCount) return p;
            }
        }
        else if (dest is PdfName name)
        {
            return LookupDestByName(state, name.Value);
        }
        else if (dest is PdfString str)
        {
            return LookupDestByName(state, Encoding.GetEncoding("iso-8859-1").GetString(str.Value));
        }

        return null;
    }

    private static int? LookupDestByName(ReaderState state, string name)
    {
        // Check /Dests in catalog
        if (state.DestsDictionary == null && state.Document.Catalog["Dests"] != null)
        {
            state.DestsDictionary = state.Document.ResolveDictionary(state.Document.Catalog["Dests"]);
        }

        if (state.DestsDictionary != null)
        {
            var val = state.Document.Resolve(state.DestsDictionary[name]);
            if (val != null)
            {
                // It could be an array or a dict with /D
                if (val is PdfDictionary dDict)
                {
                    val = state.Document.Resolve(dDict["D"]);
                }
                if (val is PdfArray arr)
                {
                    if (arr.Count > 0 && arr[0] is PdfReference pr)
                    {
                        var pageDict = state.Document.ResolveDictionary(pr);
                        if (pageDict != null)
                        {
                            for (int i = 0; i < state.Document.PageCount; i++)
                            {
                                if (state.Document.Pages[i].Dictionary == pageDict) return i;
                            }
                        }
                    }
                }
            }
        }

        // Check /Names -> /Dests name tree
        if (!state.NamesDestsAttempted)
        {
            state.NamesDestsAttempted = true;
            var names = state.Document.ResolveDictionary(state.Document.Catalog["Names"]);
            if (names != null)
            {
                state.NamesDestsTree = state.Document.ResolveDictionary(names["Dests"]);
            }
        }

        if (state.NamesDestsTree != null)
        {
            var result = SearchNameTree(state, state.NamesDestsTree, name, 0);
            if (result != null) return result;
        }

        return null;
    }

    private static int? SearchNameTree(ReaderState state, PdfDictionary node, string name, int depth)
    {
        if (depth > 50) return null; // protection

        var names = state.Document.Resolve(node["Names"]) as PdfArray;
        if (names != null)
        {
            for (int i = 0; i < names.Count; i += 2)
            {
                var keyStr = state.Document.Resolve(names[i]) as PdfString;
                if (keyStr != null)
                {
                    // The PDF standard says name tree keys for Dests are text strings
                    string key = ExtractTitle(keyStr);
                    if (key == name)
                    {
                        var val = state.Document.Resolve(names[i + 1]);
                        if (val is PdfDictionary dDict)
                        {
                            val = state.Document.Resolve(dDict["D"]);
                        }
                        if (val is PdfArray arr && arr.Count > 0 && arr[0] is PdfReference pr)
                        {
                            var pageDict = state.Document.ResolveDictionary(pr);
                            if (pageDict != null)
                            {
                                for (int p = 0; p < state.Document.PageCount; p++)
                                {
                                    if (state.Document.Pages[p].Dictionary == pageDict) return p;
                                }
                            }
                        }
                        return null; // found key but no valid dest array
                    }
                }
            }
        }

        var kids = state.Document.Resolve(node["Kids"]) as PdfArray;
        if (kids != null)
        {
            foreach (var kid in kids.Items)
            {
                var kidNode = state.Document.ResolveDictionary(kid);
                if (kidNode != null)
                {
                    var res = SearchNameTree(state, kidNode, name, depth + 1);
                    if (res != null) return res;
                }
            }
        }

        return null;
    }

    private sealed class ReaderState
    {
        public PdfDocument Document { get; }
        public HashSet<int> VisitedNodes { get; } = new();
        public HashSet<PdfDictionary> VisitedDicts { get; } = new(ReferenceEqualityComparer.Instance);
        public int ItemCount;

        public PdfDictionary? DestsDictionary;
        public bool NamesDestsAttempted;
        public PdfDictionary? NamesDestsTree;

        public ReaderState(PdfDocument document)
        {
            Document = document;
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
                result.Add(new PdfOutlineItem(item.Title, null, remappedChildren));
            }
            else
            {
                int? mappedIndex = newIndex(item.PageIndex.Value);
                if (mappedIndex.HasValue)
                {
                    result.Add(new PdfOutlineItem(item.Title, mappedIndex, remappedChildren));
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
