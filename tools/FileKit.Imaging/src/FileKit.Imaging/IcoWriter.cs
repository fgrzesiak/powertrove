using System.Buffers.Binary;

namespace FileKit.Imaging;

public sealed record IcoEntry(int Size, byte[] Png);

/// <summary>Vista+ ICO container with validated PNG entries, at most seven sizes up to 256 px.</summary>
public static class IcoWriter
{
    public static void Write(Stream destination, IReadOnlyList<IcoEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination); ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count is < 1 or > 7 || entries.Select(e => e.Size).Distinct().Count() != entries.Count)
            throw new ImageProbeException("Invalid icon entry count.");
        foreach (var entry in entries)
        {
            if (entry.Size is < 1 or > 256 || entry.Png is null || entry.Png.Length > 2 * 1024 * 1024)
                throw new ImageProbeException("Invalid icon entry.");
            var info = ImageProbe.Probe(entry.Png);
            if (info.Format != ImageFormat.Png || info.Width != entry.Size || info.Height != entry.Size)
                throw new ImageProbeException("Icon entry dimensions do not match.");
        }
        ct.ThrowIfCancellationRequested();
        using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)entries.Count);
        uint offset = (uint)(6 + entries.Count * 16);
        foreach (var entry in entries)
        {
            writer.Write((byte)(entry.Size == 256 ? 0 : entry.Size)); writer.Write((byte)(entry.Size == 256 ? 0 : entry.Size));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write((uint)entry.Png.Length); writer.Write(offset); offset += (uint)entry.Png.Length;
        }
        foreach (var entry in entries) { ct.ThrowIfCancellationRequested(); writer.Write(entry.Png); }
    }

    /// <summary>
    /// Writes square images as an icon the way Windows does: 256 px as PNG, smaller sizes as 32-bit
    /// DIBs with alpha and an AND mask. The Windows Imaging Component and older shell code read PNG
    /// entries only at 256 px.
    /// </summary>
    public static void WriteImages(Stream destination, IReadOnlyList<Raster.RasterImage> images, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination); ArgumentNullException.ThrowIfNull(images);
        if (images.Count is < 1 or > 7 || images.Any(i => i.Width != i.Height || i.Width is < 1 or > 256)
            || images.Select(i => i.Width).Distinct().Count() != images.Count)
            throw new ImageProbeException("Invalid icon images.");
        var payloads = new List<(int Size, byte[] Data)>();
        foreach (var image in images)
        {
            ct.ThrowIfCancellationRequested();
            if (image.Width == 256)
            {
                using var png = new MemoryStream(); Png.PngEncoder.Encode(image, png, ct: ct); payloads.Add((256, png.ToArray()));
            }
            else
            {
                payloads.Add((image.Width, Dib(image)));
            }
        }
        using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)payloads.Count);
        uint offset = (uint)(6 + payloads.Count * 16);
        foreach (var (size, data) in payloads)
        {
            writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write((uint)data.Length); writer.Write(offset); offset += (uint)data.Length;
        }
        foreach (var (_, data) in payloads) writer.Write(data);
    }

    /// <summary>BITMAPINFOHEADER + bottom-up BGRA pixels + 1-bit AND mask (set where fully transparent).</summary>
    internal static byte[] Dib(Raster.RasterImage image)
    {
        int size = image.Width, maskStride = ((size + 31) / 32) * 4;
        var data = new byte[40 + size * size * 4 + maskStride * size];
        BinaryPrimitives.WriteInt32LittleEndian(data, 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), size);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), size * 2); // XOR + AND bitmaps
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20), size * size * 4 + maskStride * size);
        int pixels = 40, mask = 40 + size * size * 4;
        for (int y = 0; y < size; y++)
        {
            int sourceRow = (size - 1 - y) * size * 4; // bottom-up
            image.Bgra.AsSpan(sourceRow, size * 4).CopyTo(data.AsSpan(pixels + y * size * 4));
            for (int x = 0; x < size; x++)
                if (image.Bgra[sourceRow + x * 4 + 3] == 0)
                    data[mask + y * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
        }
        return data;
    }
}
