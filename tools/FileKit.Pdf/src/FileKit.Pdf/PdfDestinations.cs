using System.Text;

namespace FileKit.Pdf;

/// <summary>A place in the document: a page index and, when the destination names one, the top of the view in PDF units.</summary>
public readonly record struct PdfDestination(int PageIndex, double? Top);

/// <summary>
/// Resolves explicit destinations (<c>[page /XYZ left top zoom]</c>, <c>/FitH top</c>, <c>/FitBH top</c>, <c>/Fit</c>),
/// named destinations from the catalog's <c>/Dests</c> and the <c>/Names /Dests</c> name tree, and GoTo actions. Name
/// tree walks are bounded in depth and visited nodes.
/// </summary>
internal sealed class PdfDestinations
{
    private const int MaximumDepth = 32;
    private const int MaximumNodes = 10_000;
    private readonly PdfDocument _document;
    private Dictionary<PdfDictionary, int>? _pages;
    private PdfDictionary? _dests;
    private PdfDictionary? _nameTree;
    private bool _namesLoaded;

    public PdfDestinations(PdfDocument document) => _document = document;

    /// <summary>The target of an outline item or link annotation: its <c>/Dest</c>, or the <c>/D</c> of a GoTo action.</summary>
    public PdfDestination? OfItem(PdfDictionary item)
    {
        if (_document.Resolve(item["Dest"]) is { } dest) return Resolve(dest);
        if (_document.ResolveDictionary(item["A"]) is { } action && action.GetName("S") == "GoTo" && _document.Resolve(action["D"]) is { } target)
            return Resolve(target);
        return null;
    }

    public PdfDestination? Resolve(PdfObject destination, int depth = 0)
    {
        if (depth > 4) return null;
        switch (_document.Resolve(destination))
        {
            case PdfArray array:
                return Explicit(array);
            case PdfDictionary dictionary when _document.Resolve(dictionary["D"]) is { } inner:
                return Resolve(inner, depth + 1);
            case PdfName name:
                return Named(name.Value, depth);
            case PdfString text:
                return Named(Text(text), depth);
            default:
                return null;
        }
    }

    private PdfDestination? Explicit(PdfArray array)
    {
        if (array.Count == 0) return null;
        int? page = array[0] switch
        {
            PdfReference reference when _document.ResolveDictionary(reference) is { } dictionary => PageIndex(dictionary),
            PdfInteger index when index.Value >= 0 && index.Value < _document.PageCount => (int)index.Value,
            _ => null
        };
        if (page is null) return null;
        string? kind = array.Count > 1 ? (_document.Resolve(array[1]) as PdfName)?.Value : null;
        int topIndex = kind switch { "XYZ" => 3, "FitH" or "FitBH" => 2, "FitR" => 5, _ => -1 };
        double? top = topIndex > 0 && topIndex < array.Count ? Number(_document.Resolve(array[topIndex])) : null;
        return new PdfDestination(page.Value, top);
    }

    internal int? PageIndex(PdfDictionary page)
    {
        if (_pages is null)
        {
            _pages = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < _document.PageCount; i++) _pages.TryAdd(_document.Pages[i].Dictionary, i);
        }
        return _pages.TryGetValue(page, out int index) ? index : null;
    }

    private PdfDestination? Named(string name, int depth)
    {
        _dests ??= _document.ResolveDictionary(_document.Catalog["Dests"]);
        if (_dests?[name] is { } direct) return Resolve(direct, depth + 1);
        if (!_namesLoaded)
        {
            _namesLoaded = true;
            _nameTree = _document.ResolveDictionary(_document.ResolveDictionary(_document.Catalog["Names"])?["Dests"]);
        }
        if (_nameTree is null) return null;
        int visited = 0;
        var value = Lookup(_nameTree, name, 0, ref visited, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance));
        return value is null ? null : Resolve(value, depth + 1);
    }

    private PdfObject? Lookup(PdfDictionary node, string name, int depth, ref int visited, HashSet<PdfDictionary> seen)
    {
        if (depth > MaximumDepth || ++visited > MaximumNodes || !seen.Add(node)) return null;
        if (_document.Resolve(node["Limits"]) is PdfArray { Count: 2 } limits &&
            _document.Resolve(limits[0]) is PdfString low && _document.Resolve(limits[1]) is PdfString high &&
            (string.CompareOrdinal(name, Text(low)) < 0 || string.CompareOrdinal(name, Text(high)) > 0))
            return null;
        if (_document.Resolve(node["Names"]) is PdfArray names)
            for (int i = 0; i + 1 < names.Count; i += 2)
                if (_document.Resolve(names[i]) is PdfString key && Text(key) == name)
                    return names[i + 1];
        if (_document.Resolve(node["Kids"]) is PdfArray kids)
            foreach (var kid in kids.Items)
                if (_document.ResolveDictionary(kid) is { } child && Lookup(child, name, depth + 1, ref visited, seen) is { } found)
                    return found;
        return null;
    }

    internal static string Text(PdfString text)
    {
        var bytes = text.Value;
        return bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
            ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
            : Encoding.Latin1.GetString(bytes);
    }

    private static double? Number(PdfObject? value) => value switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => null
    };
}
