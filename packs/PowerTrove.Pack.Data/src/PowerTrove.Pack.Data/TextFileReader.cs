using System.Text;

namespace PowerTrove.Pack.Data;

public sealed record TextFile(string Text, Encoding Encoding, bool HasBom);

/// <summary>Bounded decoding with explicit BOM precedence and strict UTF-8 before Windows-1252.</summary>
public static class TextFileReader
{
    public const long MaxBytes = 200L * 1024 * 1024;
    public static async Task<TextFile> ReadAsync(Stream input, CancellationToken cancellationToken = default, long maxBytes = MaxBytes)
    {
        if (maxBytes < 0 || maxBytes > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        using var memory = new MemoryStream(); byte[] buffer = new byte[81920]; int n;
        while ((n = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + n > maxBytes) throw new FileProcessingException(SR.Get("TooLarge"));
            memory.Write(buffer, 0, n);
        }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = memory.ToArray();
        (Encoding? encoding, int skip) = bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) ? ((Encoding?)new UTF32Encoding(false, true, true), 4)
            : bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }) ? (new UTF32Encoding(true, true, true), 4)
            : bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? (new UTF8Encoding(true, true), 3)
            : bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) ? (new UnicodeEncoding(false, true, true), 2)
            : bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }) ? (new UnicodeEncoding(true, true, true), 2) : (null, 0);
        try
        {
            if (encoding is not null) return new(encoding.GetString(bytes, skip, bytes.Length - skip), encoding, true);
            try { return new(new UTF8Encoding(false, true).GetString(bytes), new UTF8Encoding(false), false); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return new(encoding.GetString(bytes), encoding, false);
            }
        }
        catch (DecoderFallbackException) { throw new FileProcessingException(SR.Get("InvalidText")); }
    }
}
