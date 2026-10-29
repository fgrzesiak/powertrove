using System.Buffers.Binary;

namespace PowerTrove.Abstractions;

/// <summary>Ephemeral input preview for an explicit editor only. Never log, persist or add to history.</summary>
public sealed record ImagePreviewData(int SourceWidth, int SourceHeight, int PixelWidth, int PixelHeight, string PngBase64)
{
    public const int MaximumEncodedLength = 1_500_000;
    public int? OriginalWidth { get; init; }
    public int? OriginalHeight { get; init; }
    public bool TryDecodePng(out byte[] bytes)
    {
        bytes = [];
        if (OriginalWidth.HasValue != OriginalHeight.HasValue || OriginalWidth is < 1 or > 30000 || OriginalHeight is < 1 or > 30000
            || (long)(OriginalWidth ?? 0) * (OriginalHeight ?? 0) > 120_000_000) return false;
        if (SourceWidth is < 1 or > 30000 || SourceHeight is < 1 or > 30000 || (long)SourceWidth * SourceHeight > 60_000_000
            || PixelWidth is < 1 or > 512 || PixelHeight is < 1 or > 512
            || PngBase64 is null || PngBase64.Length is < 40 or > MaximumEncodedLength) return false;
        try { bytes = Convert.FromBase64String(PngBase64); }
        catch (FormatException) { return false; }
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) != PixelWidth
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) != PixelHeight)
        { bytes = []; return false; }
        return true;
    }
    public override string ToString() => "ImagePreviewData { Payload = redacted }";
}
