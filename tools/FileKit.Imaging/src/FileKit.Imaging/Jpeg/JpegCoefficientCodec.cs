using System.Buffers.Binary;

namespace FileKit.Imaging.Jpeg;

/// <summary>Lossless geometry applied to quantized JPEG DCT coefficients.</summary>
public enum JpegCoefficientTransform
{
    Identity, Rotate90, Rotate180, Rotate270, FlipHorizontal, FlipVertical, Transpose, Transverse
}

public sealed class JpegCoefficientException(string message) : Exception(message);

public sealed record JpegMarker(byte Marker, byte[] Data);

public sealed class JpegCoefficientComponent
{
    internal JpegCoefficientComponent(int id, int horizontal, int vertical, int quantization)
    {
        Id = id; HorizontalSampling = horizontal; VerticalSampling = vertical; QuantizationTable = quantization;
    }

    public int Id { get; }
    public int HorizontalSampling { get; internal set; }
    public int VerticalSampling { get; internal set; }
    public int QuantizationTable { get; }
    public int BlockWidth { get; internal set; }
    public int BlockHeight { get; internal set; }
    public short[] Coefficients { get; internal set; } = [];

    internal Span<short> Block(int x, int y) =>
        Coefficients.AsSpan(checked((y * BlockWidth + x) * 64), 64);
}

/// <summary>
/// Parsed JPEG coefficients. Callers may compare all coefficients without decoding RGB.
/// </summary>
public sealed class JpegCoefficientImage
{
    internal readonly Dictionary<int, ushort[]> Quantization = new();
    internal readonly List<JpegMarker> Metadata = [];
    internal readonly List<JpegCoefficientComponent> ComponentsInternal = [];

    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public bool Progressive { get; internal set; }
    public int RestartInterval { get; set; }
    public IReadOnlyList<JpegCoefficientComponent> Components => ComponentsInternal;
    public IReadOnlyList<JpegMarker> Markers => Metadata;
}

public static partial class JpegCoefficientCodec
{
    /// <summary>Limits shared with the existing PowerTrove lossless-transform adapter.</summary>
    public const int MaxInputBytes = 300 * 1024 * 1024;
    public const int MaxOutputBytes = 300 * 1024 * 1024;
    public const int MaxDimension = 30000;
    public const long MaxPixels = 60_000_000;
    public const long MaxCoefficients = 96_000_000;
    public const int MaxScans = 32;
    public const int MaxHeaderBytes = 1024 * 1024;

    internal static readonly int[] Zigzag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    ];
    internal static int U16(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset > bytes.Length - 2) throw new JpegCoefficientException("Truncated JPEG segment.");
        return BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
    }

    internal static void W16(Span<byte> bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes[offset..], checked((ushort)value));

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new JpegCoefficientException(message);
    }

    public static bool SwapsAxes(JpegCoefficientTransform operation) => operation is
        JpegCoefficientTransform.Rotate90 or JpegCoefficientTransform.Rotate270 or
        JpegCoefficientTransform.Transpose or JpegCoefficientTransform.Transverse;

    public static JpegCoefficientImage TransformCoefficients(
        JpegCoefficientImage source, JpegCoefficientTransform operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (operation == JpegCoefficientTransform.Identity) return source;
        int maxH = source.Components.Max(c => c.HorizontalSampling);
        int maxV = source.Components.Max(c => c.VerticalSampling);
        // Not even one partial edge MCU is permitted for geometric transforms.
        Check(source.Width % (8 * maxH) == 0 && source.Height % (8 * maxV) == 0,
            "Lossless transform requires complete MCUs without cropping.");
        bool swap = SwapsAxes(operation);
        var output = new JpegCoefficientImage
        {
            Width = swap ? source.Height : source.Width,
            Height = swap ? source.Width : source.Height,
            Progressive = false,
            RestartInterval = source.RestartInterval
        };
        foreach (var metadata in source.Metadata)
            output.Metadata.Add(new JpegMarker(metadata.Marker, metadata.Data.ToArray()));
        foreach (var (key, table) in source.Quantization)
        {
            var target = new ushort[64];
            for (int v = 0; v < 8; v++)
                for (int u = 0; u < 8; u++)
                    target[v * 8 + u] = table[swap ? u * 8 + v : v * 8 + u];
            output.Quantization.Add(key, target);
        }
        foreach (var component in source.Components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int width = component.BlockWidth, height = component.BlockHeight;
            var transformed = new JpegCoefficientComponent(component.Id,
                swap ? component.VerticalSampling : component.HorizontalSampling,
                swap ? component.HorizontalSampling : component.VerticalSampling,
                component.QuantizationTable)
            {
                BlockWidth = swap ? height : width,
                BlockHeight = swap ? width : height,
                Coefficients = new short[component.Coefficients.Length]
            };
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    (int tx, int ty) = operation switch
                    {
                        JpegCoefficientTransform.Rotate90 => (height - 1 - y, x),
                        JpegCoefficientTransform.Rotate180 => (width - 1 - x, height - 1 - y),
                        JpegCoefficientTransform.Rotate270 => (y, width - 1 - x),
                        JpegCoefficientTransform.FlipHorizontal => (width - 1 - x, y),
                        JpegCoefficientTransform.FlipVertical => (x, height - 1 - y),
                        JpegCoefficientTransform.Transpose => (y, x),
                        JpegCoefficientTransform.Transverse => (height - 1 - y, width - 1 - x),
                        _ => (x, y)
                    };
                    var input = component.Block(x, y);
                    var target = transformed.Block(tx, ty);
                    for (int v = 0; v < 8; v++)
                        for (int u = 0; u < 8; u++)
                        {
                            int value = input[swap ? u * 8 + v : v * 8 + u];
                            bool negative = operation switch
                            {
                                JpegCoefficientTransform.Rotate90 or JpegCoefficientTransform.FlipHorizontal => (u & 1) != 0,
                                JpegCoefficientTransform.Rotate270 or JpegCoefficientTransform.FlipVertical => (v & 1) != 0,
                                JpegCoefficientTransform.Rotate180 or JpegCoefficientTransform.Transverse => ((u + v) & 1) != 0,
                                _ => false
                            };
                            target[v * 8 + u] = checked((short)(negative ? -value : value));
                        }
                }
            }
            output.ComponentsInternal.Add(transformed);
        }
        return output;
    }

    public static byte[] Transform(ReadOnlyMemory<byte> bytes, JpegCoefficientTransform operation,
        Func<byte, byte[], int, int, bool, byte[]>? normalizeMetadata = null,
        CancellationToken cancellationToken = default)
    {
        var image = Decode(bytes, cancellationToken);
        var transformed = TransformCoefficients(image, operation, cancellationToken);
        bool swapped = SwapsAxes(operation);
        if (normalizeMetadata is not null)
        {
            for (int i = 0; i < transformed.Metadata.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var marker = transformed.Metadata[i];
                transformed.Metadata[i] = new JpegMarker(marker.Marker, normalizeMetadata(marker.Marker,
                    marker.Data, transformed.Width, transformed.Height, swapped));
            }
        }
        return Encode(transformed, cancellationToken);
    }
}
