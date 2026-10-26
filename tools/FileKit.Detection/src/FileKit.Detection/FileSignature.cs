using System.Buffers.Binary;
using System.Text;

namespace FileKit.Detection;

public enum FileFormat
{
    Unknown,
    Jpeg,
    Png,
    Gif,
    Bmp,
    Tiff,
    WebP,
    Avif,
    Heic,
    Ico,
    Pdf,
    Zip,
    Gzip,
    SevenZip,
    Rar,
    Mp3,
    Aac,
    Wav,
    Flac,
    Ogg,
    /// <summary>ISO base media file format (MP4, M4A, MOV, 3GP...). See <see cref="DetectionResult.Subtype"/> for the major brand.</summary>
    IsoMedia,
    /// <summary>Matroska or WebM. <see cref="DetectionResult.Subtype"/> is "webm" when the document type says so.</summary>
    Matroska,
    Avi,
    /// <summary>Advanced Systems Format (WMV, WMA).</summary>
    Asf,
    WindowsExecutable,
    Tar
}

/// <param name="Format">Detected container or codec family.</param>
/// <param name="Subtype">Optional detail, e.g. an ISO brand ("M4A ") or Matroska doc type ("webm").</param>
public sealed record DetectionResult(FileFormat Format, string? Subtype = null)
{
    public static DetectionResult Unknown { get; } = new(FileFormat.Unknown);

    public bool IsKnown => Format != FileFormat.Unknown;
}

/// <summary>
/// Detects file formats from their leading bytes. Never trusts file names, never
/// decodes content and never reads more than <see cref="HeaderLength"/> bytes.
/// </summary>
public static class FileSignature
{
    /// <summary>Bytes inspected. PDF allows up to 1024 bytes of leading garbage before "%PDF-".</summary>
    public const int HeaderLength = 1024;

    private static readonly string[] HeifBrands = { "heic", "heix", "heim", "heis", "hevc", "hevx", "hevm", "hevs" };
    private static readonly string[] AvifBrands = { "avif", "avis" };

    public static DetectionResult Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length < 2)
            return DetectionResult.Unknown;

        // Checked first: an ftyp box size such as 0x00000100 would otherwise look like an ICO header.
        if (header.Length >= 12 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
            return DetectIsoMedia(header);

        if (StartsWith(header, 0xFF, 0xD8, 0xFF)) return new(FileFormat.Jpeg);
        if (StartsWith(header, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return new(FileFormat.Png);
        if (StartsWithAscii(header, "GIF87a") || StartsWithAscii(header, "GIF89a")) return new(FileFormat.Gif);
        if (StartsWith(header, 0x49, 0x49, 0x2A, 0x00) || StartsWith(header, 0x4D, 0x4D, 0x00, 0x2A)) return new(FileFormat.Tiff);
        if (StartsWith(header, 0x00, 0x00, 0x01, 0x00) && header.Length >= 6 && header[4] + header[5] > 0) return new(FileFormat.Ico);
        if (IsBmp(header)) return new(FileFormat.Bmp);

        if (StartsWithAscii(header, "RIFF") && header.Length >= 12)
        {
            var form = header.Slice(8, 4);
            if (form.SequenceEqual("WEBP"u8)) return new(FileFormat.WebP);
            if (form.SequenceEqual("WAVE"u8)) return new(FileFormat.Wav);
            if (form.SequenceEqual("AVI "u8)) return new(FileFormat.Avi);
            return DetectionResult.Unknown;
        }

        if (StartsWithAscii(header, "fLaC")) return new(FileFormat.Flac);
        if (StartsWithAscii(header, "OggS")) return new(FileFormat.Ogg);
        if (StartsWith(header, 0x1A, 0x45, 0xDF, 0xA3))
            return new(FileFormat.Matroska, ContainsAscii(header, "webm") ? "webm" : "matroska");
        if (StartsWith(header, 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11)) return new(FileFormat.Asf);

        if (StartsWith(header, 0x50, 0x4B, 0x03, 0x04) || StartsWith(header, 0x50, 0x4B, 0x05, 0x06) || StartsWith(header, 0x50, 0x4B, 0x07, 0x08))
            return new(FileFormat.Zip);
        if (header.Length >= 262 && header.Slice(257, 5).SequenceEqual("ustar"u8)) return new(FileFormat.Tar);
        if (StartsWith(header, 0x1F, 0x8B)) return new(FileFormat.Gzip);
        if (StartsWith(header, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return new(FileFormat.SevenZip);
        if (StartsWithAscii(header, "Rar!\u001A\u0007")) return new(FileFormat.Rar);
        if (StartsWithAscii(header, "MZ")) return new(FileFormat.WindowsExecutable);

        if (IndexOfAscii(header, "%PDF-") >= 0) return new(FileFormat.Pdf);

        if (StartsWithAscii(header, "ID3")) return new(FileFormat.Mp3);
        var mpeg = DetectMpegAudioFrame(header);
        if (mpeg.IsKnown) return mpeg;

        return DetectionResult.Unknown;
    }

    /// <summary>Reads at most <see cref="HeaderLength"/> bytes from the current position.</summary>
    public static async Task<DetectionResult> DetectAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[HeaderLength];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return Detect(buffer.AsSpan(0, total));
    }

    public static async Task<DetectionResult> DetectFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await DetectAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static DetectionResult DetectIsoMedia(ReadOnlySpan<byte> header)
    {
        string major = Encoding.ASCII.GetString(header.Slice(8, 4));
        uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(header);
        int end = (int)Math.Min(Math.Max(boxSize, 16u), (uint)header.Length);

        var brands = new List<string> { major };
        for (int offset = 16; offset + 4 <= end; offset += 4)
            brands.Add(Encoding.ASCII.GetString(header.Slice(offset, 4)));

        if (brands.Any(b => AvifBrands.Contains(b))) return new(FileFormat.Avif, major);
        if (brands.Any(b => HeifBrands.Contains(b))) return new(FileFormat.Heic, major);
        // "mif1"/"msf1" alone are generic HEIF image containers.
        if (major is "mif1" or "msf1") return new(FileFormat.Heic, major);
        return new(FileFormat.IsoMedia, major);
    }

    private static DetectionResult DetectMpegAudioFrame(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4 || header[0] != 0xFF || (header[1] & 0xE0) != 0xE0)
            return DetectionResult.Unknown;

        int version = (header[1] >> 3) & 0x3;
        int layer = (header[1] >> 1) & 0x3;
        if (layer == 0)
        {
            // ADTS (AAC): sync 0xFFF, layer always 0.
            return (header[1] & 0xF6) == 0xF0 ? new(FileFormat.Aac) : DetectionResult.Unknown;
        }

        int bitrateIndex = (header[2] >> 4) & 0xF;
        int sampleRateIndex = (header[2] >> 2) & 0x3;
        if (version == 1 || bitrateIndex == 0xF || bitrateIndex == 0 || sampleRateIndex == 0x3)
            return DetectionResult.Unknown;
        return new(FileFormat.Mp3);
    }

    private static bool IsBmp(ReadOnlySpan<byte> header)
    {
        if (header.Length < 18 || header[0] != (byte)'B' || header[1] != (byte)'M')
            return false;
        uint dibHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(14, 4));
        return dibHeaderSize is 12 or 40 or 52 or 56 or 64 or 108 or 124;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, params byte[] prefix) =>
        data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);

    private static bool StartsWithAscii(ReadOnlySpan<byte> data, string prefix) =>
        data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(Encoding.ASCII.GetBytes(prefix));

    private static int IndexOfAscii(ReadOnlySpan<byte> data, string value) =>
        data.IndexOf(Encoding.ASCII.GetBytes(value));

    private static bool ContainsAscii(ReadOnlySpan<byte> data, string value) => IndexOfAscii(data, value) >= 0;
}
