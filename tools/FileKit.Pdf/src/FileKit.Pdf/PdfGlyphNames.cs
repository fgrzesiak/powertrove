using System.Globalization;
using System.Text;

namespace FileKit.Pdf;

/// <summary>Adobe Glyph List and PDF standard encodings loaded from bundled data (no NuGet).</summary>
internal static class PdfGlyphNames
{
    private static readonly Lazy<Dictionary<string, string>> Glyphs = new(LoadGlyphs);
    private static readonly Lazy<Dictionary<string, string?[]>> Encodings = new(LoadEncodings);

    public static string? FromName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string normalized = name.Split('.')[0];
        if (normalized.IndexOf('_') >= 0)
        {
            var values = normalized.Split('_').Select(FromName).ToArray();
            return values.All(v => v is not null) ? string.Concat(values) : null;
        }
        if (Glyphs.Value.TryGetValue(normalized, out var result)) return result;
        if (normalized.StartsWith("uni", StringComparison.Ordinal) &&
            normalized.Length >= 7 && (normalized.Length - 3) % 4 == 0)
        {
            var builder = new StringBuilder();
            for (int i = 3; i < normalized.Length; i += 4)
            {
                if (!ushort.TryParse(normalized.AsSpan(i, 4), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out ushort unit)) return null;
                builder.Append((char)unit);
            }
            string value = builder.ToString();
            return value.Any(char.IsSurrogate) && !IsValidSurrogates(value) ? null : value;
        }
        if (normalized.StartsWith('u') && normalized.Length is >= 5 and <= 7 &&
            int.TryParse(normalized.AsSpan(1), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out int codePoint) && Rune.IsValid(codePoint))
            return char.ConvertFromUtf32(codePoint);
        if (normalized.Length == 1 && char.IsLetterOrDigit(normalized[0])) return normalized;
        return null;
    }

    private static bool IsValidSurrogates(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false;
            }
            else if (char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }

    public static string? FromEncoding(string? encoding, int code)
    {
        if (code is < 0 or > 255) return null;
        string key = encoding switch
        {
            "WinAnsiEncoding" or "WinAnsi" => "WinAnsiEncoding",
            "MacRomanEncoding" or "MacRoman" => "MacRomanEncoding",
            _ => "StandardEncoding"
        };
        return Encodings.Value.TryGetValue(key, out var array) ? array[code] : null;
    }

    private static Dictionary<string, string> LoadGlyphs()
    {
        using var reader = Reader("glyphlist.txt");
        var glyphs = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int separator = line.IndexOf(';');
            if (separator <= 0) continue;
            string hex = line[(separator + 1)..];
            var builder = new StringBuilder();
            bool valid = true;
            foreach (string cp in hex.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(cp, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                        out int number) || !Rune.IsValid(number))
                { valid = false; break; }
                builder.Append(char.ConvertFromUtf32(number));
            }
            if (valid) glyphs[line[..separator]] = builder.ToString();
        }
        return glyphs;
    }

    private static Dictionary<string, string?[]> LoadEncodings()
    {
        using var reader = Reader("pdf-encodings.txt");
        var tables = new Dictionary<string, string?[]>(StringComparer.Ordinal);
        string? current = null;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            if (line.IndexOf(';') < 0)
            {
                current = line;
                tables.Add(current, new string?[256]);
                continue;
            }
            if (current is null) throw new InvalidDataException("Malformed encoding resource.");
            string[] entry = line.Split(';', 2);
            int code = int.Parse(entry[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            tables[current][code] = FromName(entry[1]);
        }
        return tables;
    }

    private static StreamReader Reader(string resource)
    {
        var stream = typeof(PdfGlyphNames).Assembly.GetManifestResourceStream(
            "FileKit.Pdf.Resources." + resource)
            ?? throw new InvalidOperationException("Missing bundled PDF glyph resource: " + resource);
        return new StreamReader(stream, Encoding.UTF8);
    }
}
