using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace FileKit.Pdf;

/// <summary>Reads bounded embedded TrueType cmap, CFF charset/encoding and Type1 Encoding tables.</summary>
internal sealed class PdfEmbeddedFontMap
{
    private readonly Dictionary<int, string> _directCodes = new();
    private readonly Dictionary<int, string> _glyphIds = new();

    public string? Code(int code) => _directCodes.GetValueOrDefault(code);
    public string? Glyph(int glyph) => _glyphIds.GetValueOrDefault(glyph);

    public static PdfEmbeddedFontMap Create(
        PdfDocument pdf, PdfDictionary font, PdfDictionary metrics, PdfDictionary? descriptor,
        Func<PdfStream, byte[]> decode, CancellationToken token)
    {
        var map = new PdfEmbeddedFontMap();
        if (pdf.Resolve(descriptor?["FontFile2"]) is PdfStream trueType)
            map.ReadTrueType(decode(trueType), token);
        if (pdf.Resolve(descriptor?["FontFile3"]) is PdfStream cff &&
            cff.Dictionary.GetName("Subtype") is ("Type1C" or "CIDFontType0C" or "OpenType"))
        {
            byte[] program = decode(cff);
            if (program.AsSpan().StartsWith("OTTO"u8))
            {
                map.ReadTrueType(program, token);
                if (SfntTable(program, "CFF "u8) is byte[] embeddedCff)
                    map.ReadCff(embeddedCff, token);
            }
            else map.ReadCff(program, token);
        }
        if (pdf.Resolve(descriptor?["FontFile"]) is PdfStream type1)
            map.ReadType1(decode(type1), token);
        return map;
    }

    private static byte[]? SfntTable(byte[] font, ReadOnlySpan<byte> tag)
    {
        if (font.Length < 12) return null;
        int count = U16(font, 4);
        if (count is < 1 or > 512) return null;
        for (int i = 0; i < count; i++)
        {
            int at = 12 + i * 16;
            if (at > font.Length - 16) return null;
            if (!font.AsSpan(at, 4).SequenceEqual(tag)) continue;
            long offset = U32(font, at + 8), length = U32(font, at + 12);
            return offset >= 0 && length > 0 && offset + length <= font.Length
                ? font.AsSpan((int)offset, (int)length).ToArray() : null;
        }
        return null;
    }

    private static int U16(ReadOnlySpan<byte> data, int pos) =>
        pos >= 0 && pos <= data.Length - 2 ? BinaryPrimitives.ReadUInt16BigEndian(data[pos..]) : -1;

    private static long U32(ReadOnlySpan<byte> data, int pos) =>
        pos >= 0 && pos <= data.Length - 4 ? BinaryPrimitives.ReadUInt32BigEndian(data[pos..]) : -1;

    private void ReadTrueType(byte[] font, CancellationToken token)
    {
        var bytes = font.AsSpan();
        if (bytes.Length < 12) return;
        int numTables = U16(bytes, 4);
        if (numTables is < 1 or > 512) return;
        int cmapStart = -1, cmapLength = 0;
        for (int i = 0; i < numTables; i++)
        {
            token.ThrowIfCancellationRequested();
            int entry = 12 + i * 16;
            if (entry > bytes.Length - 16) return;
            if (bytes.Slice(entry, 4).SequenceEqual("cmap"u8))
            {
                long offset = U32(bytes, entry + 8), length = U32(bytes, entry + 12);
                if (offset < 0 || length < 4 || offset + length > bytes.Length) return;
                cmapStart = (int)offset;
                cmapLength = (int)length;
                break;
            }
        }
        if (cmapStart < 0) return;
        var cmap = bytes.Slice(cmapStart, cmapLength);
        int count = U16(cmap, 2);
        if (count is < 1 or > 100) return;
        int best = -1, bestRank = -1;
        for (int i = 0; i < count; i++)
        {
            int record = 4 + i * 8;
            if (record > cmap.Length - 8) break;
            int platform = U16(cmap, record), encoding = U16(cmap, record + 2);
            int rank = platform == 3 && encoding == 10 ? 4
                : platform == 0 ? 3 : platform == 3 && encoding == 1 ? 2
                : platform == 3 && encoding == 0 ? 1 : 0;
            long offset = U32(cmap, record + 4);
            if (rank > bestRank && offset >= 0 && offset <= cmap.Length - 4)
            {
                int format = U16(cmap, (int)offset);
                if (format is 0 or 4 or 6 or 12) { best = (int)offset; bestRank = rank; }
            }
        }
        if (best < 0) return;
        var subtable = cmap[best..];
        switch (U16(subtable, 0))
        {
            case 0: ReadCmap0(subtable, token); break;
            case 4: ReadCmap4(subtable, token); break;
            case 6: ReadCmap6(subtable, token); break;
            case 12: ReadCmap12(subtable, token); break;
        }
    }

    private void AssignGlyph(int glyphId, int unicode)
    {
        if (glyphId is <= 0 or > 65535 || !Rune.IsValid(unicode)) return;
        // Prefer ordinary Unicode codepoints over private-use or symbol aliases.
        bool common = unicode is >= 0x20 and < 0xE000 or >= 0xF900 and <= 0x10FFFF;
        if (!_glyphIds.ContainsKey(glyphId) || common)
            _glyphIds[glyphId] = char.ConvertFromUtf32(unicode);
        if (unicode <= 0xFF && !_directCodes.ContainsKey(unicode))
            _directCodes[unicode] = char.ConvertFromUtf32(unicode);
    }

    private void ReadCmap0(ReadOnlySpan<byte> data, CancellationToken token)
    {
        if (data.Length < 262) return;
        for (int code = 0; code < 256; code++)
        {
            token.ThrowIfCancellationRequested();
            AssignGlyph(data[6 + code], code);
        }
    }

    private void ReadCmap6(ReadOnlySpan<byte> data, CancellationToken token)
    {
        if (data.Length < 10) return;
        int first = U16(data, 6), count = U16(data, 8);
        if (count < 0 || 10 + count * 2 > data.Length) return;
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            AssignGlyph(U16(data, 10 + i * 2), first + i);
        }
    }

    private void ReadCmap4(ReadOnlySpan<byte> data, CancellationToken token)
    {
        int length = U16(data, 2), segments = U16(data, 6) / 2;
        if (length < 24 || length > data.Length || segments is < 1 or > 8192) return;
        data = data[..length];
        int endStart = 14, startStart = endStart + segments * 2 + 2;
        int deltaStart = startStart + segments * 2, rangeStart = deltaStart + segments * 2;
        if (rangeStart + segments * 2 > data.Length) return;
        for (int i = 0; i < segments; i++)
        {
            token.ThrowIfCancellationRequested();
            int first = U16(data, startStart + i * 2), last = U16(data, endStart + i * 2);
            if (first < 0 || last < first) continue;
            int delta = U16(data, deltaStart + i * 2);
            int offset = U16(data, rangeStart + i * 2);
            for (int code = first; code <= last && code < 65535; code++)
            {
                int glyph = offset == 0 ? (code + delta) & 65535
                    : U16(data, rangeStart + i * 2 + offset + (code - first) * 2);
                if (glyph > 0 && offset != 0) glyph = (glyph + delta) & 65535;
                AssignGlyph(glyph, code);
            }
        }
    }

    private void ReadCmap12(ReadOnlySpan<byte> data, CancellationToken token)
    {
        if (data.Length < 16) return;
        long size = U32(data, 4), groups = U32(data, 12);
        if (size < 16 || size > data.Length || groups is < 1 or > 100_000 || 16 + groups * 12 > size) return;
        int assigned = 0;
        for (int i = 0; i < groups; i++)
        {
            token.ThrowIfCancellationRequested();
            int p = 16 + i * 12;
            long first = U32(data, p), last = U32(data, p + 4), glyph = U32(data, p + 8);
            if (last < first || last - first > 65536) continue;
            for (long code = first; code <= last && assigned < 200_000; code++, assigned++)
                AssignGlyph((int)(glyph + code - first), (int)code);
        }
    }

    private void ReadType1(byte[] program, CancellationToken token)
    {
        // PFA and the cleartext section of PFB expose Encoding before eexec.
        byte[] prefix = program.Length > 1 && program[0] == 0x80 && program[1] == 1 &&
                        program.Length >= 6 ? program.AsSpan(6, Math.Min(program.Length - 6, 1_000_000)).ToArray()
                        : program.AsSpan(0, Math.Min(program.Length, 1_000_000)).ToArray();
        string text = Encoding.Latin1.GetString(prefix);
        int end = text.IndexOf("eexec", StringComparison.Ordinal);
        if (end >= 0) text = text[..end];
        if (text.Contains("/Encoding StandardEncoding", StringComparison.Ordinal))
            for (int i = 0; i < 256; i++)
                if (PdfGlyphNames.FromEncoding("StandardEncoding", i) is { } known) _directCodes[i] = known;
        foreach (Match match in Regex.Matches(text,
                     @"\bdup\s+(\d{1,3})\s+/([A-Za-z0-9_.]+)\s+put\b",
                     RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
        {
            token.ThrowIfCancellationRequested();
            if (!int.TryParse(match.Groups[1].Value, out int code) || code > 255) continue;
            if (PdfGlyphNames.FromName(match.Groups[2].Value) is { } unicode) _directCodes[code] = unicode;
        }
    }

    private static readonly Lazy<string[]> CffStrings = new(() =>
    {
        var resource = typeof(PdfEmbeddedFontMap).Assembly.GetManifestResourceStream(
            "FileKit.Pdf.Resources.cff-standard-strings.txt")
            ?? throw new InvalidOperationException("CFF names resource missing.");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    });

    private readonly record struct IndexItem(int Start, int Length);

    private static bool ReadIndex(ReadOnlySpan<byte> bytes, ref int at, out IndexItem[] index)
    {
        index = [];
        if (at < 0 || at > bytes.Length - 2) return false;
        int count = U16(bytes, at); at += 2;
        if (count == 0) return true;
        if (count > 100_000 || at >= bytes.Length) return false;
        int offSize = bytes[at++];
        if (offSize is < 1 or > 4 || (long)(count + 1) * offSize > bytes.Length - at) return false;
        var offsets = new long[count + 1];
        for (int i = 0; i < offsets.Length; i++)
        {
            long n = 0;
            for (int k = 0; k < offSize; k++) n = (n << 8) | bytes[at++];
            offsets[i] = n;
        }
        int baseAt = at;
        if (offsets[0] != 1 || offsets[^1] < 1 || offsets[^1] - 1 > bytes.Length - baseAt) return false;
        index = new IndexItem[count];
        for (int i = 0; i < count; i++)
        {
            if (offsets[i] < 1 || offsets[i + 1] < offsets[i]) return false;
            index[i] = new IndexItem(baseAt + (int)offsets[i] - 1,
                (int)(offsets[i + 1] - offsets[i]));
        }
        at = checked(baseAt + (int)offsets[^1] - 1);
        return true;
    }

    private static Dictionary<int, int> TopDictionary(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<int, int>();
        var operands = new List<int>();
        for (int i = 0; i < data.Length;)
        {
            byte b = data[i++];
            if (b <= 21)
            {
                int op = b;
                if (b == 12 && i < data.Length) op = 0xC00 | data[i++];
                if (operands.Count > 0) result[op] = operands[^1];
                operands.Clear();
                continue;
            }
            int number;
            if (b is >= 32 and <= 246) number = b - 139;
            else if (b is >= 247 and <= 250 && i < data.Length) number = (b - 247) * 256 + data[i++] + 108;
            else if (b is >= 251 and <= 254 && i < data.Length) number = -(b - 251) * 256 - data[i++] - 108;
            else if (b == 28 && i <= data.Length - 2) { number = (short)U16(data, i); i += 2; }
            else if (b == 29 && i <= data.Length - 4) { number = (int)U32(data, i); i += 4; }
            else if (b == 30)
            {
                while (i < data.Length && (data[i++] & 0x0F) != 0x0F) { }
                number = 0;
            }
            else if (b == 255 && i <= data.Length - 4) { number = (int)U32(data, i) >> 16; i += 4; }
            else break;
            operands.Add(number);
            if (operands.Count > 100) break;
        }
        return result;
    }

    private void ReadCff(byte[] font, CancellationToken token)
    {
        ReadOnlySpan<byte> bytes = font;
        if (bytes.Length < 4 || bytes[0] != 1 || bytes[2] < 4) return;
        int at = bytes[2];
        if (!ReadIndex(bytes, ref at, out _) ||
            !ReadIndex(bytes, ref at, out var top) || top.Length == 0 ||
            !ReadIndex(bytes, ref at, out var customStrings) ||
            !ReadIndex(bytes, ref at, out _)) return;
        var topDict = TopDictionary(bytes.Slice(top[0].Start, top[0].Length));
        if (!topDict.TryGetValue(17, out int charStrings) || charStrings < 0) return;
        // A CID-keyed font (ROS operator) has CIDs in its charset, not glyph names.
        if (topDict.ContainsKey(0xC1E)) return;
        if (!ReadIndex(bytes, ref charStrings, out var glyphs) || glyphs.Length is < 1 or > 65536) return;
        string SidName(int sid)
        {
            string[] standard = CffStrings.Value;
            if (sid < standard.Length) return standard[sid];
            int index = sid - standard.Length;
            return index >= 0 && index < customStrings.Length
                ? Encoding.Latin1.GetString(font.AsSpan(customStrings[index].Start, customStrings[index].Length))
                : ".notdef";
        }
        int[] sids = new int[glyphs.Length];
        for (int i = 1; i < sids.Length; i++) sids[i] = i;
        int charset = topDict.GetValueOrDefault(15);
        if (charset > 2 && charset < bytes.Length)
        {
            int pos = charset, format = bytes[pos++];
            for (int gid = 1; gid < sids.Length && pos < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                if (format == 0) { if (pos + 2 > bytes.Length) return; sids[gid++] = U16(bytes, pos); pos += 2; }
                else if (format is 1 or 2)
                {
                    int sid = U16(bytes, pos); pos += 2;
                    if (sid < 0 || pos >= bytes.Length) return;
                    int remaining = format == 1 ? bytes[pos++] : U16(bytes, pos);
                    if (format == 2) pos += 2;
                    if (remaining < 0) return;
                    for (int n = 0; n <= remaining && gid < sids.Length; n++) sids[gid++] = sid + n;
                }
                else return;
            }
        }
        for (int gid = 1; gid < sids.Length; gid++)
        {
            token.ThrowIfCancellationRequested();
            if (PdfGlyphNames.FromName(SidName(sids[gid])) is { } mapped) _glyphIds[gid] = mapped;
        }
        int encoding = topDict.GetValueOrDefault(16);
        if (encoding == 0)
        {
            for (int code = 0; code < 256; code++)
                if (PdfGlyphNames.FromEncoding("StandardEncoding", code) is { } unicode)
                    _directCodes.TryAdd(code, unicode);
            return;
        }
        if (encoding <= 1 || encoding >= bytes.Length) return;
        int atEncoding = encoding, formatByte = bytes[atEncoding++];
        int count = atEncoding < bytes.Length ? bytes[atEncoding++] : 0;
        int id = 1;
        if ((formatByte & 127) == 0)
        {
            for (int i = 0; i < count && atEncoding < bytes.Length && id < glyphs.Length; i++)
            {
                if (_glyphIds.GetValueOrDefault(id++) is { } unicode)
                    _directCodes[bytes[atEncoding]] = unicode;
                atEncoding++;
            }
        }
        else if ((formatByte & 127) == 1)
        {
            for (int i = 0; i < count && atEncoding + 2 <= bytes.Length; i++)
            {
                int first = bytes[atEncoding++], nLeft = bytes[atEncoding++];
                for (int n = 0; n <= nLeft && id < glyphs.Length; n++)
                    if (_glyphIds.GetValueOrDefault(id++) is { } unicode)
                        _directCodes[(first + n) & 255] = unicode;
            }
        }
        if ((formatByte & 0x80) != 0 && atEncoding < bytes.Length)
        {
            int supplements = bytes[atEncoding++];
            for (int i = 0; i < supplements && atEncoding + 3 <= bytes.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                int code = bytes[atEncoding++], sid = U16(bytes, atEncoding);
                atEncoding += 2;
                if (PdfGlyphNames.FromName(SidName(sid)) is { } unicode)
                    _directCodes[code] = unicode;
            }
        }
    }
}
