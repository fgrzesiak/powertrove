namespace FileKit.Pdf;

/// <summary>A piece of page content an element owns: the marked-content identifier on a page.</summary>
public readonly record struct PdfMarkedContent(int PageIndex, int Mcid);

/// <summary>
/// An element of a tagged PDF's logical structure. <see cref="Type"/> is the standard structure type after the
/// document's role map (H1, P, L, LI, Lbl, LBody, Table, TR, TH, TD, Figure, Caption, TOC, TOCI, Code, Quote, ...).
/// <see cref="Content"/> is the element's own content in order; children follow in <see cref="Children"/>.
/// </summary>
public sealed record PdfStructureElement(string Type, IReadOnlyList<PdfStructureElement> Children, IReadOnlyList<PdfMarkedContent> Content,
    string? Alt = null, int RowSpan = 1, int ColumnSpan = 1)
{
    public override string ToString() => $"PdfStructureElement {{ Type = {Type}, Children = {Children.Count} }}";
}

/// <summary>
/// Reads the structure tree of a tagged PDF (<c>/StructTreeRoot</c>). Kids may be marked-content identifiers (on the
/// element's page), marked-content references, object references (skipped) or elements; custom types follow the role
/// map to a standard type. Depth, element count and cycles are bounded; a damaged tree yields what was read.
/// </summary>
public static class PdfStructure
{
    private const int MaximumDepth = 100;
    private const int MaximumElements = 200_000;
    private const int MaximumContent = 2_000_000;

    public static IReadOnlyList<PdfStructureElement> Read(PdfDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.ResolveDictionary(document.Catalog["StructTreeRoot"]);
        if (root is null) return [];
        var reader = new Reader(document, document.ResolveDictionary(root["RoleMap"]), cancellationToken);
        try { return reader.Kids(root["K"], null, 0); }
        catch (Exception ex) when (ex is PdfFormatException or InvalidCastException or ArgumentException) { return reader.Partial; }
    }

    private sealed class Reader(PdfDocument document, PdfDictionary? roleMap, CancellationToken ct)
    {
        private readonly PdfDestinations _pages = new(document);
        private readonly HashSet<PdfDictionary> _seen = new(ReferenceEqualityComparer.Instance);
        private int _elements, _content;
        public IReadOnlyList<PdfStructureElement> Partial { get; private set; } = [];

        public List<PdfStructureElement> Kids(PdfObject? kids, int? page, int depth)
        {
            var result = new List<PdfStructureElement>();
            if (depth == 0) Partial = result;
            foreach (var kid in Items(kids))
            {
                ct.ThrowIfCancellationRequested();
                if (document.ResolveDictionary(kid) is { } dictionary && Element(dictionary, page, depth) is { } element) result.Add(element);
            }
            return result;
        }

        private IEnumerable<PdfObject> Items(PdfObject? kids) => document.Resolve(kids) switch
        {
            PdfArray array => array.Items,
            null => [],
            var single => [single]
        };

        private PdfStructureElement? Element(PdfDictionary dictionary, int? page, int depth)
        {
            if (depth > MaximumDepth || ++_elements > MaximumElements || dictionary.GetName("Type") is "MCR" or "OBJR") return null;
            if (!_seen.Add(dictionary)) return null;
            string type = Standard(dictionary.GetName("S") ?? "");
            int? ownPage = document.ResolveDictionary(dictionary["Pg"]) is { } pageDictionary ? _pages.PageIndex(pageDictionary) : page;
            var content = new List<PdfMarkedContent>();
            var children = new List<PdfStructureElement>();
            foreach (var kid in Items(dictionary["K"]))
            {
                ct.ThrowIfCancellationRequested();
                switch (document.Resolve(kid))
                {
                    case PdfInteger mcid when ownPage is int index && ++_content <= MaximumContent:
                        content.Add(new PdfMarkedContent(index, (int)mcid.Value));
                        break;
                    case PdfDictionary { } reference when reference.GetName("Type") == "MCR":
                        int? referencePage = document.ResolveDictionary(reference["Pg"]) is { } p ? _pages.PageIndex(p) : ownPage;
                        if (referencePage is int at && document.Resolve(reference["MCID"]) is PdfInteger id && ++_content <= MaximumContent)
                            content.Add(new PdfMarkedContent(at, (int)id.Value));
                        break;
                    case PdfDictionary child when child.GetName("Type") != "OBJR":
                        if (Element(child, ownPage, depth + 1) is { } element) children.Add(element);
                        break;
                }
            }
            string? alt = document.Resolve(dictionary["Alt"]) is PdfString text ? PdfDestinations.Text(text).Trim() : null;
            var (rows, columns) = Spans(dictionary);
            return new PdfStructureElement(type, children, content, string.IsNullOrEmpty(alt) ? null : alt, rows, columns);
        }

        /// <summary>RowSpan and ColSpan from the element's table attributes (one dictionary or an array of them).</summary>
        private (int Rows, int Columns) Spans(PdfDictionary element)
        {
            int rows = 1, columns = 1;
            foreach (var item in Items(element["A"]))
                if (document.ResolveDictionary(item) is { } attributes)
                {
                    if (document.Resolve(attributes["RowSpan"]) is PdfInteger r) rows = (int)Math.Clamp(r.Value, 1, 100);
                    if (document.Resolve(attributes["ColSpan"]) is PdfInteger c) columns = (int)Math.Clamp(c.Value, 1, 100);
                }
            return (rows, columns);
        }

        /// <summary>Follows the role map (at most eight steps, as maps may chain) to a standard type.</summary>
        private string Standard(string type)
        {
            for (int i = 0; i < 8 && roleMap?[type] is { } mapped && document.Resolve(mapped) is PdfName name && name.Value != type; i++)
                type = name.Value;
            return type;
        }
    }
}
