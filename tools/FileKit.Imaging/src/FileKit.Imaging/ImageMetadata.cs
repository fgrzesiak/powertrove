using System.Buffers.Binary;

namespace FileKit.Imaging;

/// <summary>Only non-identifying facts. Null means the container does not expose a supported fact.</summary>
public sealed record ImageMetadataFlags(bool? HasLocation, bool? HasColorProfile, int? FrameCount, double? DpiX, double? DpiY, bool HasAlpha);

public static partial class ImageProbe
{
    public const long MaxMetadataFileBytes = 300L * 1024 * 1024;

    public static ImageMetadataFlags ProbeMetadataFlags(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxMetadataFileBytes) throw new ImageProbeException("Image metadata exceeds the supported limit.");
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        return ScanMetadata(stream, CancellationToken.None);
    }

    /// <summary>Scans container structure, seeking over pixel payloads; no image or identifying metadata is retained.</summary>
    public static Task<ImageMetadataFlags> ProbeMetadataFlagsAsync(Stream stream, CancellationToken ct = default) =>
        Task.Run(() => ScanMetadata(stream, ct), ct);

    private static ImageMetadataFlags ScanMetadata(Stream stream, CancellationToken ct)
    {
        if (!stream.CanSeek || stream.Length - stream.Position > MaxMetadataFileBytes) throw new ImageProbeException("Metadata requires a bounded seekable stream.");
        long start = stream.Position; long end = stream.Length;
        byte[] head = Read(12); stream.Position = start;
        bool gps = false, icc = false, alpha = false; double? dpiX = null, dpiY = null;
        try
        {
            if (head[0] == 255 && head[1] == 216)
            {
                Skip(2);
                for (int guard = 0; guard < 65536; guard++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Read(1)[0] != 255) throw Bad();
                    byte marker; do { marker = Read(1)[0]; } while (marker == 255);
                    if (marker is 0xDA or 0xD9) return new(gps, icc, 1, dpiX, dpiY, false);
                    if (marker == 0x01 || marker is >= 0xD0 and <= 0xD8) continue;
                    int length = BinaryPrimitives.ReadUInt16BigEndian(Read(2)); if (length < 2) throw Bad();
                    byte[] segment = Read(length - 2);
                    if (marker == 0xE1 && segment.AsSpan().StartsWith("Exif\0\0"u8)) ReadExif(segment.AsSpan(6));
                    if (marker == 0xE2 && segment.AsSpan().StartsWith("ICC_PROFILE\0"u8)) icc = true;
                    if (marker == 0xE0 && segment.Length >= 12 && segment.AsSpan().StartsWith("JFIF\0"u8))
                    {
                        double factor = segment[7] == 1 ? 1 : segment[7] == 2 ? 2.54 : 0;
                        SetDpi(BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(8)) * factor, BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(10)) * factor);
                    }
                }
                throw Bad();
            }
            if (head.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                Skip(8);
                for (int guard = 0; guard < 100000; guard++)
                {
                    var chunk = Read(8); uint length = BinaryPrimitives.ReadUInt32BigEndian(chunk); long next = PositionAfter(length + 4L);
                    var type = chunk.AsSpan(4, 4);
                    if (type.SequenceEqual("IHDR"u8)) { if (length != 13) throw Bad(); byte[] header = Read(13); alpha |= header[9] is 4 or 6; }
                    else if (type.SequenceEqual("iCCP"u8)) icc = true;
                    else if (type.SequenceEqual("tRNS"u8)) alpha = true;
                    else if (type.SequenceEqual("eXIf"u8)) { if (length > MaxHeaderBytes) throw Bad(); ReadExif(Read((int)length)); }
                    else if (type.SequenceEqual("pHYs"u8) && length == 9)
                    {
                        var density = Read(9); if (density[8] == 1) SetDpi(BinaryPrimitives.ReadUInt32BigEndian(density) * 0.0254, BinaryPrimitives.ReadUInt32BigEndian(density.AsSpan(4)) * 0.0254);
                    }
                    bool last = type.SequenceEqual("IEND"u8); stream.Position = next;
                    if (last) return new(gps, icc, 1, dpiX, dpiY, alpha);
                }
                throw Bad();
            }
            if (head.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || head.AsSpan(0, 6).SequenceEqual("GIF89a"u8))
            {
                byte[] header = Read(13); if ((header[10] & 128) != 0) Skip(3L * (1 << ((header[10] & 7) + 1)));
                int frames = 0;
                for (int guard = 0; guard < 100000; guard++)
                {
                    byte marker = Read(1)[0];
                    if (marker == 0x3B) return new(false, false, frames, null, null, alpha);
                    if (marker == 0x21)
                    {
                        byte label = Read(1)[0];
                        if (label == 0xF9)
                        {
                            int size = Read(1)[0]; var block = Read(size); if (size != 4) throw Bad(); alpha |= (block[0] & 1) != 0;
                            if (Read(1)[0] != 0) throw Bad();
                        }
                        else SubBlocks();
                    }
                    else if (marker == 0x2C)
                    {
                        var descriptor = Read(9); if ((descriptor[8] & 128) != 0) Skip(3L * (1 << ((descriptor[8] & 7) + 1)));
                        Skip(1); SubBlocks(); frames++;
                    }
                    else throw Bad();
                }
                throw Bad();
            }
            if (head.AsSpan(0, 4).SequenceEqual("RIFF"u8) && head.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            {
                long riffEnd = start + BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) + 8L;
                if (riffEnd > end || riffEnd < start + 12) throw Bad();
                stream.Position = start + 12; int frames = 0;
                for (int guard = 0; stream.Position < riffEnd && guard < 100000; guard++)
                {
                    var chunk = Read(8); uint length = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4)); long next = PositionAfter((long)length + (length & 1));
                    if (next > riffEnd) throw Bad(); var type = chunk.AsSpan(0, 4);
                    if (type.SequenceEqual("ICCP"u8)) icc = true;
                    else if (type.SequenceEqual("ANMF"u8)) frames++;
                    else if (type.SequenceEqual("ALPH"u8)) alpha = true;
                    else if (type.SequenceEqual("VP8X"u8) && length >= 10) alpha |= (Read(1)[0] & 16) != 0;
                    else if (type.SequenceEqual("VP8L"u8) && length >= 5) { var bits = Read(5); alpha |= (bits[4] & 16) != 0; }
                    else if (type.SequenceEqual("EXIF"u8))
                    {
                        if (length > MaxHeaderBytes) throw Bad(); var exif = Read((int)length);
                        ReadExif(exif.AsSpan().StartsWith("Exif\0\0"u8) ? exif.AsSpan(6) : exif);
                    }
                    stream.Position = next;
                }
                if (stream.Position != riffEnd) throw Bad();
                return new(gps, icc, Math.Max(1, frames), dpiX, dpiY, alpha);
            }
            if (head.AsSpan(0, 4).SequenceEqual("II*\0"u8) || head.AsSpan(0, 4).SequenceEqual("MM\0*"u8))
            {
                int length = (int)Math.Min(MaxHeaderBytes, end - start); ReadExif(Read(length));
                return new(gps, icc, null, dpiX, dpiY, alpha);
            }
            if (head[0] == 'B' && head[1] == 'M')
            {
                var bmp = Read((int)Math.Min(54, end - start)); if (bmp.Length < 26) throw Bad();
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(14));
                int bitsAt = size == 12 ? 24 : 28; if (bmp.Length < bitsAt + 2) throw Bad();
                alpha = BinaryPrimitives.ReadUInt16LittleEndian(bmp.AsSpan(bitsAt)) == 32;
                if (size >= 40 && bmp.Length >= 54) SetDpi(BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(38)) * 0.0254, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(42)) * 0.0254);
                return new(false, null, 1, dpiX, dpiY, alpha);
            }
            return new(null, null, null, null, null, true);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentOutOfRangeException or OverflowException)
        { throw Bad(); }

        byte[] Read(int count)
        {
            ct.ThrowIfCancellationRequested(); if (count < 0 || stream.Position + count > end) throw Bad();
            var data = new byte[count]; stream.ReadExactly(data); return data;
        }
        long PositionAfter(long count) { ct.ThrowIfCancellationRequested(); if (count < 0 || count > end - stream.Position) throw Bad(); return stream.Position + count; }
        void Skip(long count) => stream.Position = PositionAfter(count);
        void SubBlocks()
        {
            for (int guard = 0; guard < 2_000_000; guard++) { int size = Read(1)[0]; if (size == 0) return; Skip(size); }
            throw Bad();
        }
        void SetDpi(double x, double y) { if (double.IsFinite(x) && double.IsFinite(y) && x > 0 && y > 0) { dpiX = x; dpiY = y; } }
        void ReadExif(ReadOnlySpan<byte> tiff)
        {
            if (tiff.Length < 8 || !(tiff[..4].SequenceEqual("II*\0"u8) || tiff[..4].SequenceEqual("MM\0*"u8))) throw Bad();
            bool little = tiff[0] == 'I'; uint offset = Read32(tiff, 4, little);
            if (offset > tiff.Length - 2) throw Bad();
            int count = Read16(tiff, (int)offset, little); if (count > 4096 || (long)offset + 2 + count * 12L > tiff.Length) throw Bad();
            double? x = null, y = null; int unit = 2;
            for (int i = 0; i < count; i++)
            {
                int p = (int)offset + 2 + i * 12, tag = Read16(tiff, p, little), type = Read16(tiff, p + 2, little);
                uint n = Read32(tiff, p + 4, little), value = Read32(tiff, p + 8, little);
                if (tag == 0x8825 && type == 4 && n == 1 && value > 0) gps = true;
                if (tag == 34675 && n > 0) icc = true;
                if (tag == 338) alpha = true;
                if (tag == 296 && type == 3 && n == 1) unit = Read16(tiff, p + 8, little);
                if (tag is 282 or 283 && type == 5 && n == 1 && value <= tiff.Length - 8)
                {
                    uint denominator = Read32(tiff, (int)value + 4, little);
                    if (denominator > 0) { double dpi = (double)Read32(tiff, (int)value, little) / denominator; if (tag == 282) x = dpi; else y = dpi; }
                }
            }
            if (x.HasValue && y.HasValue && unit is 2 or 3) SetDpi(x.Value * (unit == 3 ? 2.54 : 1), y.Value * (unit == 3 ? 2.54 : 1));
        }
    }
    private static ImageProbeException Bad() => new("The image metadata is damaged or exceeds the supported limit.");
}
