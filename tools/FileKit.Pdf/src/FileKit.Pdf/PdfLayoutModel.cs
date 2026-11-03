using System.Text;

namespace FileKit.Pdf;

/// <summary>
/// FileKit's dependency-free positioned PDF layer. Coordinates are PDF points (bottom-left origin). <c>Mcid</c> is the
/// marked-content identifier the text or image is drawn in (it links to the structure tree of a tagged PDF), and
/// <c>Artifact</c> marks content the PDF declares as not part of the text, such as running headers and page numbers.
/// A word's <c>RaisedFrom</c> is the index in its text where trailing characters set smaller and above the baseline
/// begin (a superscript such as the footnote mark in "word¹"), or -1.
/// </summary>
public sealed record PdfLayoutGlyph(string Text, double Left, double Bottom, double Right, double Top,
    double FontSize, bool Bold, bool Italic, bool Monospace, bool Horizontal = true, int? Mcid = null, bool Artifact = false);
public sealed record PdfLayoutWord(string Text, double Left, double Bottom, double Right, double Top,
    double FontSize, bool Bold, bool Italic, bool Monospace, int? Mcid = null, bool Artifact = false, int RaisedFrom = -1);
public sealed record PdfLayoutLine(IReadOnlyList<PdfLayoutWord> Words);
public sealed record PdfLayoutBlock(IReadOnlyList<PdfLayoutLine> Lines);
public sealed record PdfLayoutRule(double X1, double Y1, double X2, double Y2);
public sealed record PdfLayoutImage(double Left, double Bottom, double Right, double Top, byte[] Bytes, string Extension,
    int? Mcid = null, bool Artifact = false);
public sealed record PdfLayoutPage(int Number, double Width, double Height,
    IReadOnlyList<PdfLayoutGlyph> Glyphs, IReadOnlyList<PdfLayoutBlock> Blocks,
    IReadOnlyList<PdfLayoutRule> Rules, IReadOnlyList<PdfLayoutImage> Images, int SkippedImages = 0,
    IReadOnlyList<PdfLayoutLink>? Links = null);
/// <summary>All pages; <c>SkippedText</c> counts characters left out because they are not horizontal or have no Unicode
/// mapping, <c>RotatedText</c> only the ones that are not horizontal.</summary>
public sealed record PdfLayoutResult(IReadOnlyList<PdfLayoutPage> Pages, int SkippedText = 0, int SkippedImages = 0, int RotatedText = 0);

public sealed record PdfLayoutOptions
{
    public int MaxPages { get; init; } = 200;
    public int MaxLetters { get; init; } = 500_000;
    public int MaxLettersPerPage { get; init; } = 100_000;
    public int MaxWords { get; init; } = 200_000;
    public int MaxWordsPerPage { get; init; } = 50_000;
    public int MaxPathCommands { get; init; } = 10_000;
    public long MaxDecodedStreamBytes { get; init; } = 16 * 1024 * 1024;
    public long MaxTotalDecodedBytes { get; init; } = 128L * 1024 * 1024;
    public int MaxFormDepth { get; init; } = 16;
    public int MaxImages { get; init; } = 500;
    public long MaxImageBytes { get; init; } = 100L * 1024 * 1024;
    public long MaxImageSamples { get; init; } = 40_000_000;
    public double MinimumImageSize { get; init; } = 24;
    public bool IncludeImages { get; init; }
    public bool SkipRotatedText { get; init; }
    /// <summary>Past <see cref="MaxPathCommands"/>, stop collecting rules on the page instead of failing.</summary>
    public bool IgnoreExcessPaths { get; init; }
}

/// <summary>PDF affine matrix (a b c d e f), with transform composition.</summary>
internal readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static PdfMatrix Identity => new(1, 0, 0, 1, 0, 0);
    public (double X, double Y) Point(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
    public PdfMatrix Multiply(PdfMatrix m) => new(
        A*m.A + C*m.B, B*m.A + D*m.B, A*m.C + C*m.D, B*m.C + D*m.D,
        A*m.E + C*m.F + E, B*m.E + D*m.F + F);
    public static PdfMatrix Translation(double x, double y) => new(1,0,0,1,x,y);
    public static PdfMatrix From(IReadOnlyList<PdfObject> args) => new(
        PdfLayoutReader.Number(args[^6]), PdfLayoutReader.Number(args[^5]),
        PdfLayoutReader.Number(args[^4]), PdfLayoutReader.Number(args[^3]),
        PdfLayoutReader.Number(args[^2]), PdfLayoutReader.Number(args[^1]));
}

/// <summary>One font instance from the active page/form resource dictionary.</summary>
internal sealed class LayoutFont
{
    private readonly Dictionary<int, string> _unicode = new();
    private readonly Dictionary<int, string> _differences = new();
    private readonly Dictionary<int, double> _widths = new();
    private readonly PdfCodeMap? _codes;
    private readonly double _defaultWidth;
    private readonly double _widthScale = 1 / 1000.0;
    private readonly string _encodingName;
    private readonly bool _builtInEncoding;
    private readonly PdfEmbeddedFontMap _embedded;
    private readonly byte[]? _cidToGlyph;

    public string Name { get; }
    public bool Bold { get; }
    public bool Italic { get; }
    public bool Mono { get; }

    public LayoutFont(PdfDocument pdf, PdfDictionary dictionary, CancellationToken token, Func<PdfStream, byte[]> decode)
    {
        string subtype = dictionary.GetName("Subtype") ?? "";
        bool cid = subtype == "Type0";
        Name = dictionary.GetName("BaseFont") ?? dictionary.GetName("Name") ?? "";
        PdfDictionary? descendant = null;
        if (cid && pdf.Resolve(dictionary["DescendantFonts"]) is PdfArray { Count: > 0 } descendants)
            descendant = pdf.ResolveDictionary(descendants[0]);
        var metrics = descendant ?? dictionary;
        var descriptor = pdf.ResolveDictionary(metrics["FontDescriptor"]);
        _embedded = PdfEmbeddedFontMap.Create(pdf, dictionary, metrics, descriptor, decode, token);
        var encoding = pdf.Resolve(dictionary["Encoding"]);
        if (cid) _codes = PdfCodeMap.Create(encoding, decode, token);
        // Without a base encoding, codes the Differences do not name keep the font program's own encoding.
        _builtInEncoding = encoding is null || encoding is PdfDictionary dict && dict["BaseEncoding"] is null;
        _encodingName = encoding switch
        {
            PdfName name => name.Value,
            PdfDictionary d => d.GetName("BaseEncoding") ?? "StandardEncoding",
            _ => subtype == "TrueType" ? "FontSpecific" : "StandardEncoding"
        };
        if (cid && pdf.Resolve(metrics["CIDToGIDMap"]) is PdfStream cidMap)
            _cidToGlyph = decode(cidMap);
        if (subtype == "Type3" && pdf.Resolve(dictionary["FontMatrix"]) is PdfArray { Count: 6 } fontMatrix)
            _widthScale = Math.Abs(Number(pdf.Resolve(fontMatrix[0]), .001));
        int flags = (int)((pdf.Resolve(descriptor?["Flags"]) as PdfInteger)?.Value ?? 0);
        Bold = (flags & (1 << 18)) != 0 || Number(pdf.Resolve(descriptor?["FontWeight"])) >= 600 ||
               Contains(Name, "Bold", "Black", "Heavy", "Semibold", "Demi");
        Italic = (flags & (1 << 6)) != 0 || Math.Abs(Number(pdf.Resolve(descriptor?["ItalicAngle"]))) > .1 ||
                 Contains(Name, "Italic", "Oblique");
        Mono = (flags & 1) != 0 || Contains(Name, "Mono", "Courier", "Consolas", "Menlo", "Cascadia");
        _defaultWidth = Number(pdf.Resolve(metrics["DW"]), cid ? 1000 : 500);
        int firstChar = (int)Number(pdf.Resolve(dictionary["FirstChar"]));
        if (!cid && pdf.Resolve(dictionary["Widths"]) is PdfArray widthArray)
            for (int i = 0; i < widthArray.Count && i < 256; i++) _widths[firstChar + i] = Number(pdf.Resolve(widthArray[i]), 500);
        if (cid && pdf.Resolve(metrics["W"]) is PdfArray widths)
            ReadCidWidths(pdf, widths, token);
        if (encoding is PdfDictionary differences && pdf.Resolve(differences["Differences"]) is PdfArray diffs)
        {
            int index = 0;
            foreach (var value in diffs.Items)
            {
                if (value is PdfInteger number) index = (int)number.Value;
                else if (value is PdfName glyph && index is >= 0 and <= 255) _differences[index++] = PdfGlyphNames.FromName(glyph.Value) ?? string.Empty;
            }
        }
        if (pdf.Resolve(dictionary["ToUnicode"]) is PdfStream cmap)
            ParseUnicode(cmap, token, decode);
    }

    private void ReadCidWidths(PdfDocument pdf, PdfArray widths, CancellationToken token)
    {
        int assigned = 0;
        for (int i = 0; i < widths.Count;)
        {
            token.ThrowIfCancellationRequested();
            if (pdf.Resolve(widths[i++]) is not PdfInteger start || i >= widths.Count) break;
            if (pdf.Resolve(widths[i]) is PdfArray array)
            {
                i++;
                for (int n = 0; n < array.Count && assigned++ < 200_000; n++) _widths[(int)start.Value + n] = Number(pdf.Resolve(array[n]), _defaultWidth);
            }
            else if (i + 1 < widths.Count)
            {
                int end = (int)Number(pdf.Resolve(widths[i++]));
                double width = Number(pdf.Resolve(widths[i++]), _defaultWidth);
                for (int n = (int)start.Value; n <= end && assigned++ < 200_000; n++) _widths[n] = width;
            }
            else break;
        }
    }

    private static bool Contains(string name, params string[] terms) =>
        terms.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The text, advance (in text space units per font size unit) and word-spacing flag of each code. Text is null
    /// when nothing maps the code to Unicode; such characters are counted, never guessed.
    /// </summary>
    public IReadOnlyList<(string? Text, double Width, bool WordSpace)> Decode(byte[] data)
    {
        var result = new List<(string?, double, bool)>(data.Length);
        for (int index = 0; index < data.Length;)
        {
            int code, cid, length;
            if (_codes is not null)
            {
                (code, length) = _codes.Next(data, index);
                cid = _codes.Cid(code, length);
            }
            else
            {
                code = cid = data[index];
                length = 1;
            }
            index += length;
            string? text;
            if (_unicode.TryGetValue(code, out string? mapped)) text = mapped;
            else if (_codes is not null)
            {
                int glyph = _cidToGlyph is null ? cid
                    : cid * 2 + 1 < _cidToGlyph.Length ? (_cidToGlyph[cid * 2] << 8) | _cidToGlyph[cid * 2 + 1] : 0;
                text = _embedded.Glyph(glyph);
            }
            else if (_differences.TryGetValue(code, out string? difference)) text = difference.Length == 0 ? null : difference;
            else if (_builtInEncoding && _embedded.Code(code) is { } embedded) text = embedded;
            else if (_encodingName is "StandardEncoding" or "WinAnsiEncoding" or "MacRomanEncoding" or "MacExpertEncoding")
                text = PdfGlyphNames.FromEncoding(_encodingName, code) ?? _embedded.Code(code);
            else text = _embedded.Code(code);
            double advance = _widths.GetValueOrDefault(_codes is null ? code : cid, _defaultWidth);
            // Word spacing applies to the single-byte code 32 only.
            result.Add((text, Math.Clamp(advance, 0, 10000) * _widthScale, length == 1 && code == 32));
        }
        return result;
    }

    private void ParseUnicode(PdfStream stream, CancellationToken ct, Func<PdfStream, byte[]> decode)
    {
        var tokens = PdfLayoutReader.Tokens(decode(stream), ct);
        int entries = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (tokens[i] is not string op || op is not ("beginbfchar" or "beginbfrange")) continue;
            bool range = op == "beginbfrange";
            for (int j = i + 1; j < tokens.Count && tokens[j] is not string; j++)
            {
                if (++entries > 65536) throw new PdfLimitExceededException("CMap complexity limit exceeded.");
                if (tokens[j] is not PdfString first || ++j >= tokens.Count) break;
                if (!range)
                {
                    if (tokens[j] is PdfString unicode) _unicode[Code(first.Value)] = Utf16(unicode.Value);
                    continue;
                }
                if (tokens[j] is not PdfString last || ++j >= tokens.Count) break;
                int start = Code(first.Value), end = Code(last.Value);
                if (end < start || end - start > 65536) throw new PdfLimitExceededException("CMap range exceeds limits.");
                if (tokens[j] is PdfString baseValue)
                {
                    var copy = (byte[])baseValue.Value.Clone();
                    for (int code = start; code <= end; code++)
                    {
                        _unicode[code] = Utf16(copy);
                        for (int k = copy.Length - 1; k >= 0; k--) if (++copy[k] != 0) break;
                    }
                }
                else if (tokens[j] is PdfArray values)
                {
                    int code = start;
                    foreach (var value in values.Items.OfType<PdfString>())
                        if (code <= end) _unicode[code++] = Utf16(value.Value);
                }
            }
        }
    }

    internal static int Code(byte[] bytes)
    {
        int result = 0;
        foreach (byte b in bytes.Take(4)) result = (result << 8) | b;
        return result;
    }

    private static string Utf16(byte[] bytes) => bytes.Length % 2 == 0
        ? Encoding.BigEndianUnicode.GetString(bytes)
        : Encoding.Latin1.GetString(bytes);

    private static double Number(PdfObject? value, double fallback = 0) => value switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => fallback
    };
}

/// <summary>
/// The encoding CMap of a Type0 font: code lengths from its codespace ranges, and code-to-CID mappings. Predefined
/// CMaps (Identity-H and the like) use two-byte codes; only the Identity CMaps map codes to CIDs directly.
/// </summary>
internal sealed class PdfCodeMap
{
    private readonly List<(int Length, byte[] Low, byte[] High)> _spaces = [];
    private readonly Dictionary<(int Length, int Code), int> _cids = new();
    private readonly List<(int Length, int First, int Last, int Cid)> _ranges = [];

    public static PdfCodeMap Create(PdfObject? encoding, Func<PdfStream, byte[]> decode, CancellationToken ct)
    {
        var map = new PdfCodeMap();
        if (encoding is PdfStream stream) map.Parse(PdfLayoutReader.Tokens(decode(stream), ct), ct);
        if (map._spaces.Count == 0) map._spaces.Add((2, [0, 0], [255, 255]));
        return map;
    }

    public (int Code, int Length) Next(byte[] data, int index)
    {
        for (int length = 1; length <= 4 && index + length <= data.Length; length++)
            foreach (var space in _spaces)
                if (space.Length == length && Within(data, index, space.Low, space.High))
                    return (Read(data, index, length), length);
        // A code outside every range uses the shortest range length, as the CMap specification requires.
        int fallback = Math.Min(_spaces.Min(s => s.Length), data.Length - index);
        return (Read(data, index, fallback), fallback);
    }

    public int Cid(int code, int length)
    {
        if (_cids.Count == 0 && _ranges.Count == 0) return code;
        if (_cids.TryGetValue((length, code), out int cid)) return cid;
        foreach (var range in _ranges)
            if (range.Length == length && code >= range.First && code <= range.Last) return range.Cid + code - range.First;
        return 0;
    }

    private static bool Within(byte[] data, int index, byte[] low, byte[] high)
    {
        for (int i = 0; i < low.Length; i++)
            if (data[index + i] < low[i] || data[index + i] > high[i]) return false;
        return true;
    }

    private static int Read(byte[] data, int index, int length)
    {
        int code = 0;
        for (int i = 0; i < length; i++) code = (code << 8) | data[index + i];
        return code;
    }

    private void Parse(List<object> tokens, CancellationToken ct)
    {
        int entries = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (tokens[i] is not string op || op is not ("begincodespacerange" or "begincidchar" or "begincidrange")) continue;
            for (int j = i + 1; j < tokens.Count && tokens[j] is not string;)
            {
                if (++entries > 100_000) throw new PdfLimitExceededException("CMap complexity limit exceeded.");
                if (tokens[j] is not PdfString first || first.Value.Length is < 1 or > 4) break;
                if (op == "begincidchar")
                {
                    if (j + 1 >= tokens.Count) break;
                    _cids[(first.Value.Length, LayoutFont.Code(first.Value))] = (int)PdfLayoutReader.Number(tokens[j + 1] as PdfObject);
                    j += 2;
                    continue;
                }
                if (j + 1 >= tokens.Count || tokens[j + 1] is not PdfString last || last.Value.Length != first.Value.Length) break;
                if (op == "begincodespacerange")
                {
                    _spaces.Add((first.Value.Length, first.Value, last.Value));
                    j += 2;
                    continue;
                }
                if (j + 2 >= tokens.Count) break;
                _ranges.Add((first.Value.Length, LayoutFont.Code(first.Value), LayoutFont.Code(last.Value), (int)PdfLayoutReader.Number(tokens[j + 2] as PdfObject)));
                j += 3;
            }
        }
    }
}
