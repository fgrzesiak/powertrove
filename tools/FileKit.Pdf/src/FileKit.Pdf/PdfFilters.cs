using System.IO.Compression;

namespace FileKit.Pdf;

/// <summary>Decoding of the filters needed to read cross-reference and object streams.</summary>
internal static class PdfFilters
{
    public static byte[] Decode(PdfStream stream, Func<PdfObject?, PdfObject?> resolve, long maxBytes)
    {
        var filters = resolve(stream.Dictionary["Filter"]) switch
        {
            null => Array.Empty<string>(),
            PdfName name => new[] { name.Value },
            PdfArray array => array.Items.Select(i => (resolve(i) as PdfName)?.Value ?? string.Empty).ToArray(),
            _ => throw new PdfFormatException("Invalid stream filter.")
        };

        var parms = resolve(stream.Dictionary["DecodeParms"]) switch
        {
            PdfDictionary d => new[] { d },
            PdfArray a => a.Items.Select(i => resolve(i) as PdfDictionary).ToArray(),
            _ => Array.Empty<PdfDictionary?>()
        };

        byte[] data = stream.Data;
        for (int i = 0; i < filters.Length; i++)
        {
            var decodeParms = i < parms.Length ? parms[i] : null;
            data = filters[i] switch
            {
                "FlateDecode" or "Fl" => ApplyPredictor(Inflate(data, maxBytes), decodeParms, resolve),
                "ASCIIHexDecode" or "AHx" => DecodeAsciiHex(data, maxBytes),
                "ASCII85Decode" or "A85" => DecodeAscii85(data, maxBytes),
                _ => throw new PdfFormatException("Unsupported filter in a structural stream.")
            };
        }
        return data;
    }

    public static byte[] DecodeAsciiHex(byte[] data, long maxBytes)
    {
        using var output = new MemoryStream(data.Length / 2);
        int high = -1;
        foreach (byte b in data)
        {
            if (b == '>') break;
            if (IsWhitespace(b)) continue;

            int v = HexValue(b);
            if (v < 0) throw new PdfFormatException("Invalid character in ASCIIHexDecode stream.");

            if (high < 0) high = v;
            else
            {
                if (output.Length >= maxBytes) throw new PdfLimitExceededException("A decoded stream expands beyond the allowed size.");
                output.WriteByte((byte)((high << 4) | v));
                high = -1;
            }
        }
        if (high >= 0)
        {
            if (output.Length >= maxBytes) throw new PdfLimitExceededException("A decoded stream expands beyond the allowed size.");
            output.WriteByte((byte)(high << 4));
        }
        return output.ToArray();
    }

    public static byte[] DecodeAscii85(byte[] data, long maxBytes)
    {
        using var output = new MemoryStream();
        int count = 0;
        uint tuple = 0;

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == '~' && i + 1 < data.Length && data[i + 1] == '>') break;
            if (IsWhitespace(b)) continue;

            if (b == 'z' && count == 0)
            {
                if (output.Length + 4 > maxBytes) throw new PdfLimitExceededException("A decoded stream expands beyond the allowed size.");
                output.WriteByte(0);
                output.WriteByte(0);
                output.WriteByte(0);
                output.WriteByte(0);
                continue;
            }
            if (b < '!' || b > 'u') throw new PdfFormatException("Invalid character in ASCII85Decode stream.");

            tuple = tuple * 85 + (uint)(b - '!');
            count++;

            if (count == 5)
            {
                if (output.Length + 4 > maxBytes) throw new PdfLimitExceededException("A decoded stream expands beyond the allowed size.");
                output.WriteByte((byte)(tuple >> 24));
                output.WriteByte((byte)(tuple >> 16));
                output.WriteByte((byte)(tuple >> 8));
                output.WriteByte((byte)tuple);
                count = 0;
                tuple = 0;
            }
        }

        if (count > 0)
        {
            if (count == 1) throw new PdfFormatException("Invalid ASCII85Decode padding.");
            for (int i = count; i < 5; i++) tuple = tuple * 85 + 84;
            if (output.Length + count - 1 > maxBytes) throw new PdfLimitExceededException("A decoded stream expands beyond the allowed size.");
            if (count > 1) output.WriteByte((byte)(tuple >> 24));
            if (count > 2) output.WriteByte((byte)(tuple >> 16));
            if (count > 3) output.WriteByte((byte)(tuple >> 8));
        }

        return output.ToArray();
    }

    private static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static int HexValue(byte c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

    public static byte[] Inflate(byte[] data, long maxBytes)
    {
        try
        {
            return InflateCore(new ZLibStream(new MemoryStream(data), CompressionMode.Decompress), maxBytes);
        }
        catch (InvalidDataException)
        {
            // Some writers emit raw deflate data or a damaged zlib trailer.
            if (data.Length < 2) throw new PdfFormatException("Damaged compressed stream.");
            try
            {
                return InflateCore(new DeflateStream(new MemoryStream(data, 2, data.Length - 2), CompressionMode.Decompress), maxBytes);
            }
            catch (InvalidDataException ex)
            {
                throw new PdfFormatException("Damaged compressed stream.", ex);
            }
        }
    }

    private static byte[] InflateCore(Stream source, long maxBytes)
    {
        using (source)
        using (var output = new MemoryStream())
        {
            var buffer = new byte[16384];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > maxBytes)
                    throw new PdfLimitExceededException("A compressed stream expands beyond the allowed size.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }

    public static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] ApplyPredictor(byte[] data, PdfDictionary? parms, Func<PdfObject?, PdfObject?> resolve)
    {
        if (parms is null) return data;
        int predictor = (int)((resolve(parms["Predictor"]) as PdfInteger)?.Value ?? 1);
        if (predictor <= 1) return data;
        int colors = (int)Math.Clamp((resolve(parms["Colors"]) as PdfInteger)?.Value ?? 1, 1, 32);
        int bits = (int)Math.Clamp((resolve(parms["BitsPerComponent"]) as PdfInteger)?.Value ?? 8, 1, 16);
        int columns = (int)Math.Clamp((resolve(parms["Columns"]) as PdfInteger)?.Value ?? 1, 1, 1 << 20);

        if (predictor == 2)
            throw new PdfFormatException("TIFF predictor is not supported in structural streams.");

        int bytesPerPixel = Math.Max(1, (colors * bits + 7) / 8);
        int rowLength = (colors * bits * columns + 7) / 8;
        return PngUnfilter(data, rowLength, bytesPerPixel);
    }

    /// <summary>Reverses PNG row filters (each row prefixed by a filter type byte).</summary>
    public static byte[] PngUnfilter(byte[] data, int rowLength, int bytesPerPixel)
    {
        int rows = data.Length / (rowLength + 1);
        var output = new byte[rows * rowLength];
        var previous = new byte[rowLength];
        for (int r = 0; r < rows; r++)
        {
            int inOffset = r * (rowLength + 1);
            int filter = data[inOffset];
            var row = output.AsSpan(r * rowLength, rowLength);
            data.AsSpan(inOffset + 1, rowLength).CopyTo(row);
            for (int i = 0; i < rowLength; i++)
            {
                int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                int up = previous[i];
                int upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                row[i] = filter switch
                {
                    0 => row[i],
                    1 => (byte)(row[i] + left),
                    2 => (byte)(row[i] + up),
                    3 => (byte)(row[i] + ((left + up) >> 1)),
                    4 => (byte)(row[i] + Paeth(left, up, upLeft)),
                    _ => throw new PdfFormatException("Invalid predictor row filter.")
                };
            }
            row.CopyTo(previous);
        }
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
