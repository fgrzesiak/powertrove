using System.Text;

namespace FileKit.Text;

/// <summary>Decoded text plus the encoding to write it back with.</summary>
public sealed record DecodedText(string Text, Encoding Encoding, bool HasBom)
{
    public override string ToString() => $"DecodedText {{ Encoding = {Encoding.WebName}, HasBom = {HasBom} }}";
}

/// <summary>
/// Bounded text decoding with explicit BOM precedence (UTF-32, UTF-8, UTF-16), then strict UTF-8, then
/// Windows-1252. Undecodable input raises <see cref="DecoderFallbackException"/>; oversized input raises
/// <see cref="TextLimitException"/>. The returned encoding's preamble is the source's BOM (empty without
/// one), so writing with it reproduces the original encoding choice.
/// </summary>
public static class TextDecoding
{
    public static async Task<DecodedText> ReadAsync(Stream input, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (maxBytes < 0 || maxBytes > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        using var memory = new MemoryStream(); byte[] buffer = new byte[81920]; int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + read > maxBytes) throw new TextLimitException("Input exceeds the text size limit.");
            memory.Write(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Decode(memory.GetBuffer().AsSpan(0, (int)memory.Length));
    }

    public static DecodedText Decode(ReadOnlySpan<byte> bytes)
    {
        (Encoding? encoding, int skip) = bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) ? ((Encoding?)new UTF32Encoding(false, true, true), 4)
            : bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }) ? (new UTF32Encoding(true, true, true), 4)
            : bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? (new UTF8Encoding(true, true), 3)
            : bytes.StartsWith(new byte[] { 0xFF, 0xFE }) ? (new UnicodeEncoding(false, true, true), 2)
            : bytes.StartsWith(new byte[] { 0xFE, 0xFF }) ? (new UnicodeEncoding(true, true, true), 2) : (null, 0);
        if (encoding is not null) return new(encoding.GetString(bytes[skip..]), encoding, true);
        try { return new(new UTF8Encoding(false, true).GetString(bytes), new UTF8Encoding(false), false); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return new(encoding.GetString(bytes), encoding, false);
        }
    }
}
