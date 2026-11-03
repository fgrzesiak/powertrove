using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileKit.Pdf;

/// <summary>
/// Bounded JPEG passthrough and PNG conversion for common PDF image XObjects: Gray, RGB, CMYK (also as ICCBased or
/// calibrated spaces) and Indexed, 1 to 16 bits, Flate/LZW/ASCII encoded, with an optional Gray soft mask as alpha.
/// A soft mask of another size is scaled to the image. Anything else (JPEG 2000, JBIG2, CCITT, TIFF predictors, other
/// Decode mappings) returns null and is counted by the caller.
/// </summary>
internal static class PdfLayoutRaster
{
    internal sealed record Raster(byte[] Bytes, string Extension);

    private const int MaxOutputBytes = 64 * 1024 * 1024;
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static Raster? TryDecode(PdfDocument pdf, PdfStream image, PdfLayoutOptions limits, CancellationToken ct, Action<long> charge)
    {
        ct.ThrowIfCancellationRequested();
        var dict = image.Dictionary;
        int width = Dimension(pdf, dict, "Width"), height = Dimension(pdf, dict, "Height");
        if (width < 1 || height < 1 || width > 30000 || height > 30000) return null;
        long pixels = (long)width * height;
        if (pixels > limits.MaxImageSamples || pixels * 4 > MaxOutputBytes) return null;
        if (pdf.Resolve(dict["ImageMask"]) is PdfBoolean { Value: true }) return null;
        var filters = Filters(pdf, dict);
        // FileKit has no JPEG decoder, so a JPEG keeps its own bytes and loses a soft mask, as other extractors do.
        if (filters.Length == 1 && filters[0] is "DCTDecode" or "DCT")
        {
            var data = image.Data;
            if (data.Length > limits.MaxDecodedStreamBytes || data.Length < 3 || data[0] != 0xFF || data[1] != 0xD8 || data[2] != 0xFF) return null;
            charge(data.Length);
            return new Raster(data.ToArray(), ".jpg");
        }
        if (filters.Any(f => f is not ("FlateDecode" or "Fl" or "LZWDecode" or "LZW" or "ASCII85Decode" or "A85" or "ASCIIHexDecode" or "AHx")))
            return null;
        int bpc = (int)Number(pdf, dict["BitsPerComponent"], 8);
        if (bpc is not (1 or 2 or 4 or 8 or 16)) return null;
        byte[]? palette = null;
        int components = Components(pdf, dict["ColorSpace"]);
        if (components == 0 && pdf.Resolve(dict["ColorSpace"]) is PdfArray { Count: >= 4 } indexed &&
            pdf.Resolve(indexed[0]) is PdfName { Value: "Indexed" or "I" })
        {
            palette = Palette(pdf, indexed, limits, ct, charge);
            if (palette is null) return null;
            components = 1;
        }
        if (components == 0) return null;
        bool? invert = DecodeArray(pdf, dict, palette is null ? 1 : (1 << bpc) - 1);
        if (invert is null) return null;
        int rowBytes = checked((int)(((long)width * components * bpc + 7) / 8));
        if ((long)rowBytes * height > limits.MaxDecodedStreamBytes) return null;
        var samples = Unfilter(pdf, dict, Decode(pdf, image, limits, ct, charge), filters, rowBytes, height, components * bpc);
        if (samples is null) return null;

        byte[]? alpha = null;
        if (pdf.Resolve(dict["SMask"]) is PdfStream mask)
        {
            alpha = Alpha(pdf, mask, width, height, limits, ct, charge);
            if (alpha is null) return null;
        }

        int channels = alpha is null ? 3 : 4;
        using var output = new MemoryStream();
        // The common case, 8-bit gray or RGB as stored, needs no per-sample conversion.
        if (palette is null && invert == false && bpc == 8 && components is 1 or 3)
        {
            WritePng(output, width, height, components == 1 && alpha is null ? 1 : channels, Interleave(samples, alpha, components, (int)pixels), ct);
            return new Raster(output.ToArray(), ".png");
        }
        var pixelsOut = new byte[checked((int)pixels * channels)];
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            int row = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int index = x * components, z = (y * width + x) * channels;
                byte r, g, b;
                if (palette is not null)
                {
                    int entry = SampleRaw(samples, row, index, bpc) * 3;
                    if (entry + 2 >= palette.Length) return null;
                    (r, g, b) = (palette[entry], palette[entry + 1], palette[entry + 2]);
                }
                else if (components == 1) r = g = b = Sample(samples, row, index, bpc);
                else if (components == 3)
                    (r, g, b) = (Sample(samples, row, index, bpc), Sample(samples, row, index + 1, bpc), Sample(samples, row, index + 2, bpc));
                else
                    (r, g, b) = Cmyk(Sample(samples, row, index, bpc), Sample(samples, row, index + 1, bpc),
                        Sample(samples, row, index + 2, bpc), Sample(samples, row, index + 3, bpc));
                if (invert == true) (r, g, b) = ((byte)(255 - r), (byte)(255 - g), (byte)(255 - b));
                pixelsOut[z] = r;
                pixelsOut[z + 1] = g;
                pixelsOut[z + 2] = b;
                if (alpha is not null) pixelsOut[z + 3] = alpha[y * width + x];
            }
        }
        WritePng(output, width, height, channels, pixelsOut, ct);
        return new Raster(output.ToArray(), ".png");
    }

    /// <summary>8-bit gray or RGB samples as PNG pixels: gray stays gray, and an alpha channel is woven in as RGBA.</summary>
    private static byte[] Interleave(byte[] samples, byte[]? alpha, int components, int pixels)
    {
        if (alpha is null) return samples;
        var rgba = new byte[checked(pixels * 4)];
        for (int i = 0, s = 0, z = 0; i < pixels; i++, z += 4)
        {
            if (components == 1) { rgba[z] = rgba[z + 1] = rgba[z + 2] = samples[s++]; }
            else { rgba[z] = samples[s]; rgba[z + 1] = samples[s + 1]; rgba[z + 2] = samples[s + 2]; s += 3; }
            rgba[z + 3] = alpha[i];
        }
        return rgba;
    }

    /// <summary>Color components of a device, calibrated or ICC-based space; 0 for anything else.</summary>
    private static int Components(PdfDocument pdf, PdfObject? space) => pdf.Resolve(space) switch
    {
        PdfName { Value: "DeviceGray" or "G" or "CalGray" } => 1,
        PdfName { Value: "DeviceRGB" or "RGB" or "CalRGB" } => 3,
        PdfName { Value: "DeviceCMYK" or "CMYK" } => 4,
        PdfArray { Count: >= 2 } array when pdf.Resolve(array[0]) is PdfName { Value: "CalGray" } => 1,
        PdfArray { Count: >= 2 } array when pdf.Resolve(array[0]) is PdfName { Value: "CalRGB" } => 3,
        PdfArray { Count: >= 2 } array when pdf.Resolve(array[0]) is PdfName { Value: "ICCBased" } &&
            pdf.Resolve(array[1]) is PdfStream profile => Number(pdf, profile.Dictionary["N"]) switch { 1 => 1, 3 => 3, 4 => 4, _ => 0 },
        _ => 0
    };

    /// <summary>An Indexed lookup table expanded to RGB triples.</summary>
    private static byte[]? Palette(PdfDocument pdf, PdfArray indexed, PdfLayoutOptions limits, CancellationToken ct, Action<long> charge)
    {
        int baseComponents = Components(pdf, indexed[1]);
        int last = (int)Number(pdf, indexed[2], -1);
        if (baseComponents == 0 || last is < 0 or > 255) return null;
        var table = pdf.Resolve(indexed[3]) switch
        {
            PdfString text => text.Value,
            PdfStream stream => Decode(pdf, stream, limits, ct, charge),
            _ => null
        };
        if (table is null || table.Length < (last + 1) * baseComponents) return null;
        var rgb = new byte[(last + 1) * 3];
        for (int i = 0; i <= last; i++)
        {
            int at = i * baseComponents;
            (rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2]) = baseComponents switch
            {
                1 => (table[at], table[at], table[at]),
                3 => (table[at], table[at + 1], table[at + 2]),
                _ => Cmyk(table[at], table[at + 1], table[at + 2], table[at + 3])
            };
        }
        return rgb;
    }

    private static byte[]? Alpha(PdfDocument pdf, PdfStream mask, int width, int height, PdfLayoutOptions limits, CancellationToken ct, Action<long> charge)
    {
        var dict = mask.Dictionary;
        int maskWidth = Dimension(pdf, dict, "Width"), maskHeight = Dimension(pdf, dict, "Height");
        if (maskWidth < 1 || maskHeight < 1 || (long)maskWidth * maskHeight > limits.MaxImageSamples) return null;
        if (Components(pdf, dict["ColorSpace"]) is not (0 or 1)) return null;
        bool? invert = DecodeArray(pdf, dict, 1);
        if (invert is null) return null;
        int bits = (int)Number(pdf, dict["BitsPerComponent"], 8);
        if (bits is not (1 or 2 or 4 or 8 or 16)) return null;
        var filters = Filters(pdf, dict);
        if (filters.Any(f => f is not ("FlateDecode" or "Fl" or "LZWDecode" or "LZW" or "ASCII85Decode" or "A85" or "ASCIIHexDecode" or "AHx")))
            return null;
        int stride = checked((int)(((long)maskWidth * bits + 7) / 8));
        var data = Unfilter(pdf, dict, Decode(pdf, mask, limits, ct, charge), filters, stride, maskHeight, bits);
        if (data is null) return null;
        // A mask of the image's size in 8 bits is the alpha channel as it is.
        if (maskWidth == width && maskHeight == height && bits == 8 && invert == false) return data;
        var alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            int row = (int)((long)y * maskHeight / height) * stride;
            for (int x = 0; x < width; x++)
            {
                byte value = Sample(data, row, (int)((long)x * maskWidth / width), bits);
                alpha[y * width + x] = invert == true ? (byte)(255 - value) : value;
            }
        }
        return alpha;
    }

    /// <summary>
    /// False for no Decode array or the default one, true when every pair is reversed (an inverted image), and null
    /// for any other mapping, which is not supported.
    /// </summary>
    private static bool? DecodeArray(PdfDocument pdf, PdfDictionary dict, double maximum)
    {
        if (pdf.Resolve(dict["Decode"]) is not PdfArray { Count: > 0 } decode || decode.Count % 2 != 0) return pdf.Resolve(dict["Decode"]) is null ? false : null;
        bool normal = true, reversed = true;
        for (int i = 0; i < decode.Count; i += 2)
        {
            double low = PdfLayoutReader.Number(pdf.Resolve(decode[i])), high = PdfLayoutReader.Number(pdf.Resolve(decode[i + 1]));
            normal &= low == 0 && high == maximum;
            reversed &= low == maximum && high == 0;
        }
        return normal ? false : reversed && maximum == 1 ? true : null;
    }

    /// <summary>Reverses a PNG predictor and checks the size; TIFF predictors fail closed rather than corrupt colors.</summary>
    private static byte[]? Unfilter(PdfDocument pdf, PdfDictionary dict, byte[] data, string[] filters, int rowBytes, int height, int bitsPerPixel)
    {
        var parameters = pdf.Resolve(dict["DecodeParms"]);
        if (parameters is PdfArray array)
        {
            int index = Array.FindLastIndex(filters, f => f is "FlateDecode" or "Fl" or "LZWDecode" or "LZW");
            parameters = index >= 0 && index < array.Count ? pdf.Resolve(array[index]) : null;
        }
        int predictor = parameters is PdfDictionary values ? (int)Number(pdf, values["Predictor"], 1) : 1;
        if (predictor is >= 10 and <= 15)
        {
            if (data.LongLength != (long)(rowBytes + 1) * height) return null;
            data = PdfFilters.PngUnfilter(data, rowBytes, Math.Max(1, (bitsPerPixel + 7) / 8));
        }
        else if (predictor != 1) return null;
        return data.LongLength == (long)rowBytes * height ? data : null;
    }

    private static (byte R, byte G, byte B) Cmyk(byte c, byte m, byte y, byte k) =>
        ((byte)((255 - c) * (255 - k) / 255), (byte)((255 - m) * (255 - k) / 255), (byte)((255 - y) * (255 - k) / 255));

    private static int Dimension(PdfDocument pdf, PdfDictionary dict, string key) => (int)Number(pdf, dict[key], 0);

    private static long Number(PdfDocument pdf, PdfObject? value, long fallback = 0) =>
        pdf.Resolve(value) is PdfInteger number ? number.Value : fallback;

    private static string[] Filters(PdfDocument pdf, PdfDictionary dict) => pdf.Resolve(dict["Filter"]) switch
    {
        PdfName name => [name.Value],
        PdfArray array => array.Items.Select(v => (pdf.Resolve(v) as PdfName)?.Value ?? "").ToArray(),
        null => [],
        _ => ["unsupported"]
    };

    private static byte[] Decode(PdfDocument pdf, PdfStream stream, PdfLayoutOptions limits, CancellationToken ct, Action<long> charge)
    {
        byte[] bytes = stream.Data;
        if (bytes.Length > limits.MaxDecodedStreamBytes) throw new PdfLimitExceededException("Encoded image exceeds size limit.");
        foreach (string filter in Filters(pdf, stream.Dictionary))
        {
            ct.ThrowIfCancellationRequested();
            bytes = filter switch
            {
                "FlateDecode" or "Fl" => PdfFilters.Inflate(bytes, limits.MaxDecodedStreamBytes),
                "LZWDecode" or "LZW" => Lzw(bytes, limits.MaxDecodedStreamBytes, ct),
                "ASCII85Decode" or "A85" => PdfFilters.DecodeAscii85(bytes, limits.MaxDecodedStreamBytes),
                "ASCIIHexDecode" or "AHx" => PdfFilters.DecodeAsciiHex(bytes, limits.MaxDecodedStreamBytes),
                _ => throw new PdfFormatException("Unsupported image filter.")
            };
        }
        charge(bytes.Length);
        return bytes;
    }

    /// <summary>PDF LZWDecode: MSB-first 9 to 12 bit codes, clear = 256, end = 257, early change.</summary>
    private static byte[] Lzw(byte[] bytes, long maxBytes, CancellationToken ct)
    {
        int bit = 0, width = 9, next = 258;
        var table = new byte[4096][];
        void Reset()
        {
            for (int i = 0; i < 256; i++) table[i] = [(byte)i];
            width = 9;
            next = 258;
        }
        Reset();
        using var output = new MemoryStream();
        byte[] previous = [];
        while (bit + width <= bytes.Length * 8)
        {
            ct.ThrowIfCancellationRequested();
            int code = 0;
            for (int k = 0; k < width; k++) code = (code << 1) | ((bytes[(bit + k) / 8] >> (7 - (bit + k) % 8)) & 1);
            bit += width;
            if (code == 257) break;
            if (code == 256)
            {
                Reset();
                previous = [];
                continue;
            }
            byte[] entry;
            if (code < next && table[code] is { } item) entry = item;
            else if (code == next && previous.Length > 0) entry = [.. previous, previous[0]];
            else throw new PdfFormatException("Invalid LZW image code.");
            if (output.Length + entry.Length > maxBytes) throw new PdfLimitExceededException("LZW output exceeded limit.");
            output.Write(entry);
            if (previous.Length > 0 && next < 4096)
            {
                table[next++] = [.. previous, entry[0]];
                if (next == (1 << width) - 1 && width < 12) width++;
            }
            previous = entry;
        }
        return output.ToArray();
    }

    private static int SampleRaw(byte[] data, int rowStart, int sampleIndex, int bits)
    {
        if (bits == 8) return data[rowStart + sampleIndex];
        int pos = rowStart + sampleIndex * bits / 8;
        if (bits == 16) return (data[pos] << 8) | data[pos + 1];
        int shift = 8 - bits - sampleIndex * bits % 8;
        return (data[pos] >> shift) & ((1 << bits) - 1);
    }

    private static byte Sample(byte[] data, int rowStart, int sampleIndex, int bits) => bits switch
    {
        8 => data[rowStart + sampleIndex],
        16 => data[rowStart + sampleIndex * 2],
        _ => (byte)(SampleRaw(data, rowStart, sampleIndex, bits) * 255 / ((1 << bits) - 1))
    };

    private static void WritePng(Stream output, int width, int height, int channels, byte[] pixels, CancellationToken ct)
    {
        const int chunk = 65536;
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = (byte)(channels switch { 1 => 0, 3 => 2, _ => 6 });
        WriteChunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        // Level 3 of zlib: within a few percent of the default level's size at a little over half its time.
        using (var z = new ZLibStream(compressed, new ZLibCompressionOptions { CompressionLevel = 3 }, true))
            for (int y = 0; y < height; y++)
            {
                ct.ThrowIfCancellationRequested();
                z.WriteByte(0);
                z.Write(pixels, y * width * channels, width * channels);
            }
        var data = compressed.GetBuffer().AsSpan(0, (int)compressed.Length);
        for (int pos = 0; pos < data.Length; pos += chunk)
        {
            ct.ThrowIfCancellationRequested();
            WriteChunk(output, "IDAT", data.Slice(pos, Math.Min(chunk, data.Length - pos)));
        }
        WriteChunk(output, "IEND", []);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        byte[] name = Encoding.ASCII.GetBytes(type);
        output.Write(name);
        output.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, name), data);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
