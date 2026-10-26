using System.Text;
using System.Text.RegularExpressions;

namespace FileKit.Pdf;

/// <summary>A page of a loaded document with inherited attributes resolved.</summary>
public sealed record PdfPage(
    int Index,
    PdfReference Reference,
    PdfDictionary Dictionary,
    PdfObject? Resources,
    PdfObject? MediaBox,
    PdfObject? CropBox,
    int Rotate);

/// <summary>
/// Read-only, in-memory view of an existing PDF. Supports classic and stream
/// cross-reference sections, object streams, incremental updates and hybrid files,
/// and reconstructs the object index by scanning when cross-references are damaged.
/// Encrypted documents are rejected with <see cref="PdfEncryptedException"/>.
/// </summary>
public sealed partial class PdfDocument
{
    private enum EntryType { Free, InFile, InObjectStream }

    private readonly record struct XrefEntry(EntryType Type, long Offset, int StreamNumber, int StreamIndex);

    private readonly byte[] _data;
    private readonly PdfReadLimits _limits;
    private Dictionary<int, XrefEntry> _xref = new();
    private readonly Dictionary<int, PdfObject> _cache = new();
    private readonly Dictionary<int, (int First, int[] Numbers, int[] Offsets, byte[] Data)> _objectStreams = new();
    private readonly HashSet<int> _loading = new();
    private readonly HashSet<int> _pageObjectNumbers = new();
    private bool _reconstructed;

    internal PdfDecryptor? Decryptor { get; }
    public bool OpenedWithEmptyPassword { get; }

    private PdfDocument(byte[] data, PdfReadLimits limits, string? password)
    {
        _data = data;
        _limits = limits;
        Version = ReadHeaderVersion();
        LoadCrossReferences();

        var encryptDict = ResolveDictionary(Trailer["Encrypt"]);
        if (encryptDict != null)
        {
            var idArray = Trailer["ID"] as PdfArray;
            byte[] fileId = idArray != null && idArray.Count > 0 && idArray[0] is PdfString s ? s.Value : Array.Empty<byte>();

            if (password != null)
            {
                Decryptor = PdfDecryptor.Create(encryptDict, fileId, password);
                if (Decryptor == null) throw new PdfPasswordRequiredException();
                OpenedWithEmptyPassword = password.Length == 0;
            }
            else
            {
                Decryptor = PdfDecryptor.Create(encryptDict, fileId, "");
                if (Decryptor != null)
                {
                    OpenedWithEmptyPassword = true;
                }
                else
                {
                    throw new PdfPasswordRequiredException();
                }
            }
        }

        Catalog = ResolveDictionary(Trailer["Root"]) ?? FindCatalogByScan()
            ?? throw new PdfFormatException("The document has no catalog.");
        Info = ResolveDictionary(Trailer["Info"]);
        Pages = LoadPages();
        if (Pages.Count == 0)
            throw new PdfFormatException("The document has no pages.");
    }

    public string Version { get; }
    public PdfDictionary Trailer { get; private set; } = new();
    public PdfDictionary Catalog { get; }
    public PdfDictionary? Info { get; }
    public IReadOnlyList<PdfPage> Pages { get; }
    public int PageCount => Pages.Count;

    /// <summary>True when the cross-reference data was damaged and rebuilt by scanning.</summary>
    public bool WasRepaired => _reconstructed;

    /// <summary>Unique id per loaded instance, used by writers to keep object maps apart.</summary>
    internal Guid InstanceId { get; } = Guid.NewGuid();

    public static PdfDocument Load(byte[] data, PdfReadLimits? limits = null, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        limits ??= PdfReadLimits.Default;
        if (data.LongLength > limits.MaxFileBytes)
            throw new PdfLimitExceededException("The file exceeds the maximum allowed size.");
        try
        {
            return new PdfDocument(data, limits, password);
        }
        catch (PdfException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidCastException or OverflowException or FormatException or InvalidOperationException)
        {
            throw new PdfFormatException("The file is damaged.", ex);
        }
    }

    public static PdfDocument Open(string path, PdfReadLimits? limits = null, string? password = null)
    {
        limits ??= PdfReadLimits.Default;
        var info = new FileInfo(path);
        if (info.Length > limits.MaxFileBytes)
            throw new PdfLimitExceededException("The file exceeds the maximum allowed size.");
        return Load(File.ReadAllBytes(path), limits, password);
    }

    public static async Task<PdfDocument> OpenAsync(string path, PdfReadLimits? limits = null, string? password = null, CancellationToken cancellationToken = default)
    {
        limits ??= PdfReadLimits.Default;
        var info = new FileInfo(path);
        if (info.Length > limits.MaxFileBytes)
            throw new PdfLimitExceededException("The file exceeds the maximum allowed size.");
        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => Load(data, limits, password), cancellationToken).ConfigureAwait(false);
    }

    internal bool IsPageObject(int objectNumber) => _pageObjectNumbers.Contains(objectNumber);

    /// <summary>Resolves indirect references (bounded chain) and returns the direct object, or null.</summary>
    public PdfObject? Resolve(PdfObject? value)
    {
        for (int hops = 0; value is PdfReference reference && hops < 32; hops++)
            value = GetObject(reference.ObjectNumber);
        return value is PdfReference or PdfNull ? null : value;
    }

    public PdfDictionary? ResolveDictionary(PdfObject? value) => Resolve(value) switch
    {
        PdfDictionary d => d,
        PdfStream s => s.Dictionary,
        _ => null
    };

    /// <summary>Loads an object by number. Missing or unreadable objects yield null (PDF semantics).</summary>
    public PdfObject? GetObject(int objectNumber)
    {
        if (_cache.TryGetValue(objectNumber, out var cached)) return cached;
        if (!_loading.Add(objectNumber)) return null; // reference cycle while loading

        try
        {
            PdfObject? value = LoadObject(objectNumber);
            if (value is null && !_reconstructed &&
                _xref.TryGetValue(objectNumber, out var entry) && entry.Type != EntryType.Free)
            {
                // A wrong offset is a typical form of damage: rebuild once and retry.
                Reconstruct();
                value = LoadObject(objectNumber);
            }
            value ??= PdfNull.Instance;
            _cache[objectNumber] = value;
            return value;
        }
        finally
        {
            _loading.Remove(objectNumber);
        }
    }

    private PdfObject? LoadObject(int objectNumber)
    {
        if (!_xref.TryGetValue(objectNumber, out var entry) || entry.Type == EntryType.Free)
            return null;

        try
        {
            if (entry.Type == EntryType.InFile)
            {
                if (entry.Offset < 0 || entry.Offset >= _data.Length) return null;
                var parser = CreateParser((int)entry.Offset);
                var (number, _, value) = parser.ParseIndirectObject();
                return number == objectNumber ? value : null;
            }

            return LoadFromObjectStream(entry.StreamNumber, entry.StreamIndex, objectNumber);
        }
        catch (PdfLimitExceededException)
        {
            throw;
        }
        catch (PdfException)
        {
            return null;
        }
    }

    private PdfObject? LoadFromObjectStream(int streamNumber, int index, int objectNumber)
    {
        if (!_objectStreams.TryGetValue(streamNumber, out var objectStream))
        {
            if (GetObject(streamNumber) is not PdfStream stream) return null;
            byte[] decoded = PdfFilters.Decode(stream, Resolve, _limits.MaxDecodedStreamBytes);
            int count = (int)Math.Clamp((Resolve(stream.Dictionary["N"]) as PdfInteger)?.Value ?? 0, 0, _limits.MaxObjects);
            int first = (int)Math.Clamp((Resolve(stream.Dictionary["First"]) as PdfInteger)?.Value ?? 0, 0, decoded.Length);
            var headerParser = new PdfParser(decoded, _limits);
            var numbers = new int[count];
            var offsets = new int[count];
            for (int i = 0; i < count; i++)
            {
                if (!headerParser.TryReadInteger(out long n) || !headerParser.TryReadInteger(out long o)) break;
                numbers[i] = (int)Math.Clamp(n, 0, int.MaxValue);
                offsets[i] = (int)Math.Clamp(o, 0, int.MaxValue);
            }
            objectStream = (first, numbers, offsets, decoded);
            _objectStreams[streamNumber] = objectStream;
        }

        // Prefer the declared index, but verify the number (indices are often wrong in damaged files).
        int slot = index >= 0 && index < objectStream.Numbers.Length && objectStream.Numbers[index] == objectNumber
            ? index
            : Array.IndexOf(objectStream.Numbers, objectNumber);
        if (slot < 0) return null;

        long position = (long)objectStream.First + objectStream.Offsets[slot];
        if (position >= objectStream.Data.Length) return null;
        var parser = new PdfParser(objectStream.Data, _limits, decryptor: Decryptor, position: (int)position);
        parser.CurrentObjectNumber = objectNumber;
        return parser.ParseObject(0);
    }

    private PdfParser CreateParser(int position) => new(_data, _limits, ResolveLength, position, Decryptor);

    private long? ResolveLength(PdfReference reference) => GetObject(reference.ObjectNumber) is PdfInteger i ? i.Value : null;

    private string ReadHeaderVersion()
    {
        int window = Math.Min(_data.Length, 1024);
        int index = _data.AsSpan(0, window).IndexOf("%PDF-"u8);
        if (index < 0)
            throw new PdfFormatException("The file is not a PDF document.");
        int start = index + 5;
        int end = start;
        while (end < _data.Length && end - start < 4 && (char.IsDigit((char)_data[end]) || _data[end] == '.')) end++;
        string version = Encoding.ASCII.GetString(_data, start, end - start);
        return version.Length == 0 ? "1.4" : version;
    }

    // ---- Cross-reference loading -------------------------------------------------

    private void LoadCrossReferences()
    {
        try
        {
            long start = FindStartXref();
            ReadXrefChain(start);
            if (Trailer["Root"] is null) throw new PdfFormatException("Missing document root.");
        }
        catch (PdfLimitExceededException)
        {
            throw;
        }
        catch (Exception ex) when (ex is PdfException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            Reconstruct();
        }
    }

    private long FindStartXref()
    {
        int searchFrom = Math.Max(0, _data.Length - 4096);
        int index = _data.AsSpan(searchFrom).LastIndexOf("startxref"u8);
        if (index < 0) throw new PdfFormatException("Missing startxref.");
        var parser = CreateParser(searchFrom + index + 9);
        if (!parser.TryReadInteger(out long offset) || offset < 0 || offset >= _data.Length)
            throw new PdfFormatException("Invalid startxref.");
        return offset;
    }

    private void ReadXrefChain(long offset)
    {
        var entries = new Dictionary<int, XrefEntry>();
        var visited = new HashSet<long>();
        PdfDictionary? newestTrailer = null;
        long? next = offset;

        while (next is long current)
        {
            if (!visited.Add(current) || visited.Count > _limits.MaxXrefSections)
                break;

            var section = new Dictionary<int, XrefEntry>();
            PdfDictionary trailer = ReadXrefSection(current, section);

            // Hybrid files: entries from /XRefStm complement the classic table.
            if (Resolve(trailer["XRefStm"]) is PdfInteger xrefStm && xrefStm.Value >= 0 && xrefStm.Value < _data.Length && visited.Add(xrefStm.Value))
            {
                var streamSection = new Dictionary<int, XrefEntry>();
                ReadXrefSection(xrefStm.Value, streamSection);
                foreach (var (number, entry) in streamSection)
                {
                    if (!section.TryGetValue(number, out var existing) || existing.Type == EntryType.Free)
                        section[number] = entry;
                }
            }

            // Newer sections are read first and win.
            foreach (var (number, entry) in section)
                entries.TryAdd(number, entry);
            if (entries.Count > _limits.MaxObjects)
                throw new PdfLimitExceededException("The document contains too many objects.");

            newestTrailer ??= trailer;
            next = Resolve(trailer["Prev"]) is PdfInteger prev && prev.Value >= 0 && prev.Value < _data.Length ? prev.Value : null;
        }

        Trailer = newestTrailer ?? throw new PdfFormatException("Missing trailer.");
        _xref = entries;
    }

    private PdfDictionary ReadXrefSection(long offset, Dictionary<int, XrefEntry> section)
    {
        var parser = CreateParser((int)offset);
        parser.SkipWhitespaceAndComments();
        if (parser.PeekKeyword() == "xref")
        {
            parser.ReadKeyword();
            return ReadXrefTable(parser, section);
        }

        var (_, _, value) = parser.ParseIndirectObject();
        if (value is not PdfStream stream || stream.Dictionary.GetName("Type") != "XRef")
            throw new PdfFormatException("Invalid cross-reference section.");
        ReadXrefStream(stream, section);
        return stream.Dictionary;
    }

    private PdfDictionary ReadXrefTable(PdfParser parser, Dictionary<int, XrefEntry> section)
    {
        while (true)
        {
            parser.SkipWhitespaceAndComments();
            if (parser.PeekKeyword() == "trailer")
            {
                parser.ReadKeyword();
                return parser.ParseObject(0) as PdfDictionary ?? throw new PdfFormatException("Invalid trailer.");
            }

            if (!parser.TryReadInteger(out long start) || !parser.TryReadInteger(out long count) || start < 0 || count < 0)
                throw new PdfFormatException("Invalid cross-reference table.");
            if (count > _limits.MaxObjects)
                throw new PdfLimitExceededException("The document contains too many objects.");

            for (long i = 0; i < count; i++)
            {
                if (!parser.TryReadInteger(out long position) || !parser.TryReadInteger(out _))
                    throw new PdfFormatException("Invalid cross-reference entry.");
                string type = parser.ReadKeyword();
                long number = start + i;
                if (number > int.MaxValue) throw new PdfFormatException("Invalid object number.");
                section.TryAdd((int)number, type == "n" && position > 0
                    ? new XrefEntry(EntryType.InFile, position, 0, 0)
                    : new XrefEntry(EntryType.Free, 0, 0, 0));
            }
        }
    }

    private void ReadXrefStream(PdfStream stream, Dictionary<int, XrefEntry> section)
    {
        var dict = stream.Dictionary;
        if (Resolve(dict["W"]) is not PdfArray w || w.Count < 3)
            throw new PdfFormatException("Invalid cross-reference stream.");
        int[] widths = w.Items.Take(3).Select(i => (int)((Resolve(i) as PdfInteger)?.Value ?? -1)).ToArray();
        if (widths.Any(x => x < 0 || x > 8))
            throw new PdfFormatException("Invalid cross-reference stream widths.");

        int size = (int)Math.Clamp((Resolve(dict["Size"]) as PdfInteger)?.Value ?? 0, 0, _limits.MaxObjects);
        var index = Resolve(dict["Index"]) as PdfArray;
        var ranges = new List<(long Start, long Count)>();
        if (index is null) ranges.Add((0, size));
        else
        {
            for (int i = 0; i + 1 < index.Count; i += 2)
                ranges.Add(((Resolve(index[i]) as PdfInteger)?.Value ?? 0, (Resolve(index[i + 1]) as PdfInteger)?.Value ?? 0));
        }

        byte[] data = PdfFilters.Decode(stream, Resolve, _limits.MaxDecodedStreamBytes);
        int entryLength = widths.Sum();
        if (entryLength == 0) return;
        int position = 0;
        foreach (var (rangeStart, rangeCount) in ranges)
        {
            if (rangeStart < 0 || rangeCount < 0 || rangeCount > _limits.MaxObjects)
                throw new PdfFormatException("Invalid cross-reference stream index.");
            for (long i = 0; i < rangeCount && position + entryLength <= data.Length; i++)
            {
                long type = widths[0] == 0 ? 1 : ReadField(data, ref position, widths[0]);
                long field2 = ReadField(data, ref position, widths[1]);
                long field3 = ReadField(data, ref position, widths[2]);
                long number = rangeStart + i;
                if (number > int.MaxValue) break;

                section.TryAdd((int)number, type switch
                {
                    1 => new XrefEntry(EntryType.InFile, field2, 0, 0),
                    2 => new XrefEntry(EntryType.InObjectStream, 0, (int)Math.Clamp(field2, 0, int.MaxValue), (int)Math.Clamp(field3, 0, int.MaxValue)),
                    _ => new XrefEntry(EntryType.Free, 0, 0, 0)
                });
            }
        }
    }

    private static long ReadField(byte[] data, ref int position, int width)
    {
        long value = 0;
        for (int i = 0; i < width; i++) value = (value << 8) | data[position++];
        return value;
    }

    // ---- Repair ---------------------------------------------------------------------

    [GeneratedRegex(@"(?<![0-9])(\d{1,10})[\x00\t\n\f\r ]+(\d{1,5})[\x00\t\n\f\r ]+obj(?![A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectHeaderPattern();

    /// <summary>Rebuilds the object index by scanning for object headers.</summary>
    private void Reconstruct()
    {
        if (_reconstructed) return;
        _reconstructed = true;
        _cache.Clear();
        _objectStreams.Clear();

        var entries = new Dictionary<int, XrefEntry>();
        string text = Encoding.Latin1.GetString(_data);
        foreach (Match match in ObjectHeaderPattern().Matches(text))
        {
            if (!int.TryParse(match.Groups[1].Value, out int number)) continue;
            entries[number] = new XrefEntry(EntryType.InFile, match.Index, 0, 0); // later definitions win
            if (entries.Count > _limits.MaxObjects)
                throw new PdfLimitExceededException("The document contains too many objects.");
        }

        _xref = entries;

        // Register objects stored in object streams that have no direct definition.
        foreach (int number in entries.Keys.ToList())
        {
            if (LoadObject(number) is PdfStream { Dictionary: var d } && d.GetName("Type") == "ObjStm")
            {
                try
                {
                    LoadFromObjectStream(number, -1, -1);
                    if (_objectStreams.TryGetValue(number, out var objectStream))
                    {
                        for (int i = 0; i < objectStream.Numbers.Length; i++)
                            _xref.TryAdd(objectStream.Numbers[i], new XrefEntry(EntryType.InObjectStream, 0, number, i));
                    }
                }
                catch (PdfFormatException) { }
            }
        }

        // Rebuild a trailer: prefer the last "trailer" dictionary, then xref stream dictionaries.
        var trailer = new PdfDictionary();
        int trailerIndex = text.LastIndexOf("trailer", StringComparison.Ordinal);
        if (trailerIndex >= 0)
        {
            try
            {
                var parser = CreateParser(trailerIndex + 7);
                if (parser.ParseObject(0) is PdfDictionary found) trailer = found;
            }
            catch (PdfFormatException) { }
        }

        if (trailer["Root"] is null)
        {
            foreach (int number in _xref.Keys)
            {
                if (LoadObject(number) is PdfStream { Dictionary: var d } && d.GetName("Type") == "XRef" && d["Root"] is not null)
                {
                    trailer = d;
                }
            }
        }

        Trailer = trailer;
    }

    private PdfDictionary? FindCatalogByScan()
    {
        if (!_reconstructed) Reconstruct();
        foreach (int number in _xref.Keys.OrderByDescending(n => n))
        {
            if (GetObject(number) is PdfDictionary d && d.GetName("Type") == "Catalog")
                return d;
        }
        return null;
    }

    // ---- Page tree ----------------------------------------------------------------------

    private IReadOnlyList<PdfPage> LoadPages()
    {
        var rootDict = ResolveDictionary(Catalog["Pages"])
            ?? throw new PdfFormatException("The document has no page tree.");
        return CollectPagesInOrder(rootDict, Catalog["Pages"] as PdfReference);
    }

    /// <summary>Iterative depth-first walk in document order with cycle and depth protection.</summary>
    private IReadOnlyList<PdfPage> CollectPagesInOrder(PdfDictionary rootDict, PdfReference? root)
    {
        var pages = new List<PdfPage>();
        var visited = new HashSet<int>();
        if (root is not null) visited.Add(root.ObjectNumber);

        var stack = new Stack<(PdfDictionary Node, PdfReference? Ref, PdfObject? Resources, PdfObject? MediaBox, PdfObject? CropBox, int Rotate, int Depth, bool IsLeaf)>();
        stack.Push((rootDict, root, null, null, null, 0, 0, false));

        while (stack.Count > 0)
        {
            var (node, reference, resources, mediaBox, cropBox, rotate, depth, isLeaf) = stack.Pop();
            if (depth > _limits.MaxNestingDepth)
                throw new PdfLimitExceededException("The page tree is nested too deeply.");

            resources = node["Resources"] ?? resources;
            mediaBox = node["MediaBox"] ?? mediaBox;
            cropBox = node["CropBox"] ?? cropBox;
            if (Resolve(node["Rotate"]) is PdfInteger r) rotate = NormalizeRotation((int)(r.Value % 360));

            if (isLeaf)
            {
                pages.Add(new PdfPage(pages.Count, reference!, node, resources, mediaBox, cropBox, rotate));
                _pageObjectNumbers.Add(reference!.ObjectNumber);
                if (pages.Count > _limits.MaxPages)
                    throw new PdfLimitExceededException("The document has too many pages.");
                continue;
            }

            if (Resolve(node["Kids"]) is not PdfArray kids)
                continue;

            for (int i = kids.Count - 1; i >= 0; i--)
            {
                if (kids[i] is not PdfReference kidRef || !visited.Add(kidRef.ObjectNumber))
                    continue;
                if (ResolveDictionary(kidRef) is not PdfDictionary kid)
                    continue;

                bool isTreeNode = kid.GetName("Type") == "Pages" || (kid["Kids"] is not null && kid.GetName("Type") != "Page");
                stack.Push((kid, kidRef, resources, mediaBox, cropBox, rotate, depth + 1, !isTreeNode));
            }
        }

        return pages;
    }

    internal static int NormalizeRotation(int degrees)
    {
        int normalized = ((degrees % 360) + 360) % 360;
        return normalized - normalized % 90;
    }
}
