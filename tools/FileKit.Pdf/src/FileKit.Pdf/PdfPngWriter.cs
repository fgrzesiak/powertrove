using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileKit.Pdf;

/// <summary>Writes non-interlaced 8-bit Gray/RGB PNGs with checked chunks and filter-free rows.</summary>
public static class PdfPngWriter
{
    public const int MaxDimension = 30_000;
    public const int MaxPixelBytes = 64 * 1024 * 1024;
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>Writes a PNG, leaving the destination open. Components must be 1 or 3.</summary>
    public static void Write(Stream output, int width, int height, int components, ReadOnlySpan<byte> pixels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        int length = ValidateDimensions(width, height, components);
        if (pixels.Length != length) throw new PdfOperationException("Image dimensions do not match the pixel buffer.");

        // Compress before writing any output so invalid input never produces a partial PNG header.
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = width * components;
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                zlib.WriteByte(0); // PNG filter None
                zlib.Write(pixels.Slice(y * stride, stride));
            }
        }

        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = (byte)(components == 1 ? 0 : 2);
        WriteChunk(output, "IHDR", header);
        var data = compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length));
        for (int offset = 0; offset < data.Length; offset += 65_536)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteChunk(output, "IDAT", data.Slice(offset, Math.Min(65_536, data.Length - offset)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    internal static int ValidateDimensions(int width, int height, int components)
    {
        if (width <= 0 || height <= 0 || components is not (1 or 3))
            throw new PdfOperationException("Invalid image dimensions or components.");
        if (width > MaxDimension || height > MaxDimension)
            throw new PdfLimitExceededException("Image dimensions exceed the extraction limits.");
        long length = (long)width * height * components;
        if (length > MaxPixelBytes)
            throw new PdfLimitExceededException("Image dimensions exceed the extraction limits.");
        return (int)length;
    }

    private static void WriteChunk(Stream output, string name, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        byte[] type = Encoding.ASCII.GetBytes(name);
        output.Write(type);
        output.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in type) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        foreach (byte value in data) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        output.Write(length);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }
}
