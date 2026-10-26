using System.Buffers.Binary;

namespace FileKit.Imaging;

public enum ImageFormat
{
    Unknown,
    Jpeg,
    Png,
    Gif,
    Bmp,
    Tiff,
    WebP,
    Heif,
    Avif
}

/// <summary>Header facts about an image. <see cref="Orientation"/> is the EXIF value (1 when absent).</summary>
public sealed record ImageInfo(ImageFormat Format, int Width, int Height, int Orientation = 1)
{
    public long PixelCount => (long)Width * Height;

    /// <summary>Width/height as displayed after applying the orientation.</summary>
    public (int Width, int Height) DisplaySize => Orientation >= 5 ? (Height, Width) : (Width, Height);
}

/// <summary>Thrown when an image header is missing, truncated or inconsistent.</summary>
public sealed class ImageProbeException : Exception
{
    public ImageProbeException(string message) : base(message) { }
}

/// <summary>
/// Reads image dimensions from headers only, without decoding pixel data, so callers
/// can reject decompression bombs before a decoder allocates memory. Reads at most
/// <see cref="MaxHeaderBytes"/> bytes and never seeks backwards.
/// </summary>
public static partial class ImageProbe
{
    public const int MaxHeaderBytes = 1024 * 1024;

    public static async Task<ImageInfo> ProbeFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await ProbeAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ImageInfo> ProbeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[MaxHeaderBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return Probe(buffer.AsSpan(0, total));
    }

    public static ImageInfo Probe(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ProbeJpeg(data);
            if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ProbePng(data);
            if (data.Length >= 10 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
                return Valid(ImageFormat.Gif, BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
            if (data.Length >= 26 && data[0] == 'B' && data[1] == 'M') return ProbeBmp(data);
            if (data.Length >= 8 && (data[..4].SequenceEqual("II*\0"u8) || data[..4].SequenceEqual("MM\0*"u8))) return ProbeTiff(data);
            if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return ProbeWebP(data);
            if (data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8)) return ProbeHeif(data);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ImageProbeException("The image header is truncated.");
        }

        throw new ImageProbeException("The image format is not recognized.");
    }

    private static ImageInfo Valid(ImageFormat format, long width, long height, int orientation = 1)
    {
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
            throw new ImageProbeException("The image has invalid dimensions.");
        return new ImageInfo(format, (int)width, (int)height, orientation);
    }

    private static ImageInfo ProbeJpeg(ReadOnlySpan<byte> data)
    {
        int position = 2;
        int orientation = 1;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF) throw new ImageProbeException("The JPEG header is damaged.");
            byte marker = data[position + 1];
            position += 2;
            if (marker == 0xFF) { position--; continue; }
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (marker is 0xD9 or 0xDA) break;

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (length < 2) throw new ImageProbeException("The JPEG header is damaged.");
            var segment = data.Slice(position + 2, Math.Min(length - 2, data.Length - position - 2));

            if (marker == 0xE1 && segment.Length > 14 && segment[..6].SequenceEqual("Exif\0\0"u8))
                orientation = ExifOrientation(segment[6..]);
            else if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && segment.Length >= 5)
                return Valid(ImageFormat.Jpeg, BinaryPrimitives.ReadUInt16BigEndian(segment[3..]), BinaryPrimitives.ReadUInt16BigEndian(segment[1..]), orientation);

            position += length;
        }
        throw new ImageProbeException("The JPEG image has no frame header.");
    }

    private static ImageInfo ProbePng(ReadOnlySpan<byte> data)
    {
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8)) throw new ImageProbeException("The PNG header is damaged.");
        return Valid(ImageFormat.Png, BinaryPrimitives.ReadUInt32BigEndian(data[16..]), BinaryPrimitives.ReadUInt32BigEndian(data[20..]));
    }

    private static ImageInfo ProbeBmp(ReadOnlySpan<byte> data)
    {
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);
        if (headerSize == 12)
            return Valid(ImageFormat.Bmp, BinaryPrimitives.ReadUInt16LittleEndian(data[18..]), BinaryPrimitives.ReadUInt16LittleEndian(data[20..]));
        int width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
        return Valid(ImageFormat.Bmp, width, Math.Abs((long)height)); // negative height = top-down
    }

    private static ImageInfo ProbeTiff(ReadOnlySpan<byte> data)
    {
        bool little = data[0] == 'I';
        uint ifd = Read32(data, 4, little);
        if (ifd + 2 > data.Length) throw new ImageProbeException("The TIFF header is outside the probed range.");
        int count = Read16(data, (int)ifd, little);
        long width = 0, height = 0;
        int orientation = 1;
        for (int i = 0; i < count && i < 1024; i++)
        {
            int entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > data.Length) break;
            int tag = Read16(data, entry, little);
            int type = Read16(data, entry + 2, little);
            long value = type == 3 ? Read16(data, entry + 8, little) : Read32(data, entry + 8, little);
            if (tag == 256) width = value;
            else if (tag == 257) height = value;
            else if (tag == 274 && value is >= 1 and <= 8) orientation = (int)value;
        }
        return Valid(ImageFormat.Tiff, width, height, orientation);
    }

    private static ImageInfo ProbeWebP(ReadOnlySpan<byte> data)
    {
        var chunk = data.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8))
            return Valid(ImageFormat.WebP, BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF);
        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (data[20] != 0x2F) throw new ImageProbeException("The WebP header is damaged.");
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
            return Valid(ImageFormat.WebP, (bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
        }
        if (chunk.SequenceEqual("VP8X"u8))
        {
            long width = 1 + (data[24] | (data[25] << 8) | (data[26] << 16));
            long height = 1 + (data[27] | (data[28] << 8) | (data[29] << 16));
            return Valid(ImageFormat.WebP, width, height);
        }
        throw new ImageProbeException("The WebP header is damaged.");
    }

    /// <summary>Walks ftyp/meta/iprp/ipco to the first 'ispe' (image spatial extents) property.</summary>
    private static ImageInfo ProbeHeif(ReadOnlySpan<byte> data)
    {
        string brand = System.Text.Encoding.ASCII.GetString(data.Slice(8, 4));
        uint ftypSize = BinaryPrimitives.ReadUInt32BigEndian(data);
        bool avif = brand is "avif" or "avis";
        for (int p = 16; p + 4 <= Math.Min(ftypSize, (uint)data.Length); p += 4)
        {
            if (data.Slice(p, 4).SequenceEqual("avif"u8)) avif = true;
        }
        var format = avif ? ImageFormat.Avif : ImageFormat.Heif;

        var meta = FindBox(data, 0, data.Length, "meta"u8);
        if (meta is not var (metaStart, metaEnd)) throw new ImageProbeException("The HEIF header has no metadata box.");
        var iprp = FindBox(data, metaStart + 4, metaEnd, "iprp"u8); // meta is a full box (4 bytes version/flags)
        if (iprp is not var (iprpStart, iprpEnd)) throw new ImageProbeException("The HEIF header has no properties.");
        var ipco = FindBox(data, iprpStart, iprpEnd, "ipco"u8);
        if (ipco is not var (ipcoStart, ipcoEnd)) throw new ImageProbeException("The HEIF header has no properties.");
        var ispe = FindBox(data, ipcoStart, ipcoEnd, "ispe"u8);
        if (ispe is not var (ispeStart, _)) throw new ImageProbeException("The HEIF header has no image size.");
        return Valid(format, BinaryPrimitives.ReadUInt32BigEndian(data[(ispeStart + 4)..]), BinaryPrimitives.ReadUInt32BigEndian(data[(ispeStart + 8)..]));
    }

    private static (int Start, int End)? FindBox(ReadOnlySpan<byte> data, int start, int end, ReadOnlySpan<byte> type)
    {
        int position = start;
        for (int guard = 0; position + 8 <= end && guard < 10_000; guard++)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            int header = 8;
            if (size == 1)
            {
                if (position + 16 > end) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(position + 8)..]);
                header = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }
            if (size < header || position + size > end) return null;
            if (data.Slice(position + 4, 4).SequenceEqual(type)) return (position + header, (int)(position + size));
            position += (int)size;
        }
        return null;
    }

    private static int ExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return 1;
        bool little = tiff[0] == 'I';
        uint ifd = Read32(tiff, 4, little);
        if (ifd + 2 > tiff.Length) return 1;
        int count = Read16(tiff, (int)ifd, little);
        for (int i = 0; i < count && i < 512; i++)
        {
            int entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length) break;
            if (Read16(tiff, entry, little) == 0x0112)
            {
                int value = Read16(tiff, entry + 8, little);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }
        return 1;
    }

    private static int Read16(ReadOnlySpan<byte> data, int offset, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static uint Read32(ReadOnlySpan<byte> data, int offset, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
