namespace FileKit.Pdf;

/// <summary>Bounded decoding of structural PDF filters for host adapters.
/// Supports Flate (including predictors), ASCIIHex and ASCII85; never invokes native codecs.</summary>
public static class PdfStreamDecoder
{
    public static byte[] Decode(PdfStream stream, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (maxBytes is < 1 or > 64L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Dictionary["DecodeParms"] is PdfDictionary parameters && parameters["Predictor"] is PdfInteger { Value: > 1 } predictor)
        {
            long columns = (parameters["Columns"] as PdfInteger)?.Value ?? 1;
            long colors = (parameters["Colors"] as PdfInteger)?.Value ?? 1;
            long bits = (parameters["BitsPerComponent"] as PdfInteger)?.Value ?? 8;
            if (predictor.Value is < 10 or > 15 || columns is < 1 or > 1048576 || colors is < 1 or > 32 || bits is not (1 or 2 or 4 or 8 or 16) || (columns * colors * bits + 7) / 8 > maxBytes)
                throw new PdfLimitExceededException("Unsupported or oversized PDF stream predictor.");
        }
        byte[] result = PdfFilters.Decode(stream, value => value, maxBytes);
        if (result.LongLength > maxBytes) throw new PdfLimitExceededException("Decoded PDF stream limit exceeded.");
        cancellationToken.ThrowIfCancellationRequested(); return result;
    }
}
