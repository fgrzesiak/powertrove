using System.Buffers.Binary;
using System.IO.Compression;
using FileKit.Imaging.Raster;

namespace FileKit.Imaging.Png;

/// <summary>8-bit indexed, RGB and RGBA PNG. IDAT is streamed in bounded chunks.</summary>
public static class PngEncoder
{
    public static void Encode(RasterImage image, Stream destination, bool includeAlpha = true, CancellationToken ct = default) =>
        Write(image.Width, image.Height, (byte)(includeAlpha ? 6 : 2), includeAlpha ? 4 : 3, destination, null, y =>
        {
            var row = new byte[image.Width * (includeAlpha ? 4 : 3)];
            for (int x = 0; x < image.Width; x++)
            {
                int s = (y * image.Width + x) * 4, d = x * (includeAlpha ? 4 : 3);
                row[d] = image.Bgra[s + 2]; row[d + 1] = image.Bgra[s + 1]; row[d + 2] = image.Bgra[s];
                if (includeAlpha) row[d + 3] = image.Bgra[s + 3];
            }
            return row;
        }, ct);

    public static void Encode(IndexedRaster image, Stream destination, CancellationToken ct = default)
    {
        RasterImage.ByteLength(image.Width, image.Height);
        if (image.Palette is null || image.Palette.Count is < 1 or > 256 || image.Indices is null || image.Indices.Length != image.Width * image.Height)
            throw new RasterException("Invalid indexed image.");
        for (int i = 0; i < image.Indices.Length; i++)
        {
            if ((i & 65535) == 0) ct.ThrowIfCancellationRequested();
            if (image.Indices[i] >= image.Palette.Count) throw new RasterException("Invalid palette index.");
        }
        Write(image.Width, image.Height, 3, 1, destination, image.Palette,
            y => image.Indices.AsSpan(y * image.Width, image.Width).ToArray(), ct);
    }

    private static void Write(int width, int height, byte type, int bytesPerPixel, Stream output, IReadOnlyList<BgraColor>? palette, Func<int, byte[]> rowAt, CancellationToken ct)
    {
        RasterImage.ByteLength(width, height); ArgumentNullException.ThrowIfNull(output); ct.ThrowIfCancellationRequested();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = type;
        Chunk(output, "IHDR"u8, header);
        if (palette is not null)
        {
            byte[] rgb = new byte[palette.Count * 3], alpha = new byte[palette.Count];
            for (int p = 0; p < palette.Count; p++) { rgb[p * 3] = palette[p].R; rgb[p * 3 + 1] = palette[p].G; rgb[p * 3 + 2] = palette[p].B; alpha[p] = palette[p].A; }
            Chunk(output, "PLTE"u8, rgb);
            if (alpha.Any(a => a != 255)) Chunk(output, "tRNS"u8, alpha);
        }
        using (var idat = new IdatStream(output))
        {
            using (var zlib = new ZLibStream(idat, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var previous = new byte[width * bytesPerPixel];
                var candidate = new byte[previous.Length]; var best = new byte[previous.Length];
                for (int y = 0; y < height; y++)
                {
                    ct.ThrowIfCancellationRequested(); var row = rowAt(y);
                    long bestScore = long.MaxValue; byte bestFilter = 0;
                    for (byte filter = 0; filter <= 4; filter++)
                    {
                        long score = 0;
                        for (int i = 0; i < row.Length; i++)
                        {
                            int a = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0, b = previous[i], c = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                            int predicted = filter switch { 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => 0 };
                            byte value = unchecked((byte)(row[i] - predicted)); candidate[i] = value; score += Math.Abs((int)(sbyte)value);
                        }
                        if (score < bestScore) { bestScore = score; bestFilter = filter; (best, candidate) = (candidate, best); }
                    }
                    zlib.WriteByte(bestFilter); zlib.Write(best); previous = row;
                }
            }
            idat.Flush();
        }
        Chunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        uint crc = (uint)value;
        for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }).ToArray();
    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> value = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(value, data.Length); output.Write(value); output.Write(type); output.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte b in type) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc); output.Write(value);
    }
    private sealed class IdatStream(Stream output) : Stream
    {
        private readonly byte[] _buffer = new byte[65536]; private int _count;
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                int size = Math.Min(data.Length, _buffer.Length - _count); data[..size].CopyTo(_buffer.AsSpan(_count)); _count += size; data = data[size..];
                if (_count == _buffer.Length) Flush();
            }
        }
        public override void Flush() { if (_count > 0) { Chunk(output, "IDAT"u8, _buffer.AsSpan(0, _count)); _count = 0; } }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
    }
}
