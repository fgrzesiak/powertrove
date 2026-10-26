using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PowerTrove.Pack.Files;

/// <summary>Reads only bounded EXIF structures, never pixels. Unsupported/malformed metadata uses the modified date.</summary>
internal static class ExifDateReader
{
    public static DateTime? Read(Stream stream, CancellationToken ct)
    {
        const int budget = 1024 * 1024;
        if (!stream.CanSeek) return null;
        try
        {
            byte[] ReadBytes(int count) { ct.ThrowIfCancellationRequested(); if (count < 0 || count > budget) throw new InvalidDataException(); var b = new byte[count]; stream.ReadExactly(b); return b; }
            var header = ReadBytes(12); stream.Position = 0;
            if (header[0] == 255 && header[1] == 216)
            {
                stream.Position = 2; int consumed = 2;
                for (int guard = 0; guard < 4096 && consumed < budget; guard++)
                {
                    var mark = ReadBytes(2); if (mark[0] != 255 || mark[1] is 0xDA or 0xD9) return null;
                    if (mark[1] == 0x01 || mark[1] is >= 0xD0 and <= 0xD8) continue;
                    int length = BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2)) - 2;
                    if (length < 0 || consumed + length + 4 > budget) return null;
                    var segment = ReadBytes(length); consumed += length + 4;
                    if (mark[1] == 0xE1 && segment.AsSpan().StartsWith("Exif\0\0"u8)) return Tiff(segment[6..]);
                }
            }
            else if (header.AsSpan(0, 4).SequenceEqual("II*\0"u8) || header.AsSpan(0, 4).SequenceEqual("MM\0*"u8))
                return Tiff(ReadBytes((int)Math.Min(budget, stream.Length)));
            else if (header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                stream.Position = 8;
                for (int guard = 0; guard < 4096; guard++)
                {
                    var chunk = ReadBytes(8); uint size = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                    if (size > stream.Length - stream.Position - 4) return null;
                    if (chunk.AsSpan(4).SequenceEqual("eXIf"u8)) return size <= budget ? Tiff(ReadBytes((int)size)) : null;
                    if (chunk.AsSpan(4).SequenceEqual("IEND"u8)) return null;
                    stream.Position += size + 4L;
                }
            }
            else if (header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            {
                stream.Position = 12;
                for (int guard = 0; guard < 4096 && stream.Position < stream.Length; guard++)
                {
                    var chunk = ReadBytes(8); uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
                    if (size > stream.Length - stream.Position) return null;
                    if (chunk.AsSpan(0, 4).SequenceEqual("EXIF"u8))
                    {
                        if (size > budget) return null; var data = ReadBytes((int)size);
                        return Tiff(data.AsSpan().StartsWith("Exif\0\0"u8) ? data[6..] : data);
                    }
                    stream.Position += size + (size & 1L);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException) { }
        return null;
    }
    private static DateTime? Tiff(byte[] data)
    {
        if (data.Length < 8 || !(data.AsSpan(0, 4).SequenceEqual("II*\0"u8) || data.AsSpan(0, 4).SequenceEqual("MM\0*"u8))) return null;
        bool little = data[0] == 'I';
        ushort U16(int p) => little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p, 2)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
        uint U32(int p) => little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
        DateTime? Directory(uint offset, bool exif)
        {
            if (offset > data.Length - 2) return null;
            int at = (int)offset, count = U16(at); if (count > 4096 || at + 2L + count * 12L > data.Length) return null;
            DateTime? fallback = null;
            for (int i = 0; i < count; i++)
            {
                int p = at + 2 + 12 * i; int tag = U16(p), type = U16(p + 2); uint n = U32(p + 4), value = U32(p + 8);
                if (!exif && tag == 0x8769 && type == 4 && n == 1)
                { if (Directory(value, true) is { } photo) return photo; }
                if (tag is 0x9003 or 0x9004 or 0x132 && type == 2 && n is >= 19 and <= 32 && value <= data.Length - n)
                {
                    string date = Encoding.ASCII.GetString(data, (int)value, 19);
                    if (DateTime.TryParseExact(date, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
                    { if (tag == 0x9003) return result; fallback = result; }
                }
            }
            return fallback;
        }
        return Directory(U32(4), false);
    }
}
