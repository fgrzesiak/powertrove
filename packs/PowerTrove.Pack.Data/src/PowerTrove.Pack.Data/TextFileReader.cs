using System.Text;
using FileKit.Text;

namespace PowerTrove.Pack.Data;

public sealed record TextFile(string Text, Encoding Encoding, bool HasBom);

/// <summary>Bounded decoding with explicit BOM precedence and strict UTF-8 before Windows-1252 (FileKit.Text).</summary>
public static class TextFileReader
{
    public const long MaxBytes = 200L * 1024 * 1024;
    public static async Task<TextFile> ReadAsync(Stream input, CancellationToken cancellationToken = default, long maxBytes = MaxBytes)
    {
        if (maxBytes < 0 || maxBytes > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        try
        {
            var text = await TextDecoding.ReadAsync(input, maxBytes, cancellationToken).ConfigureAwait(false);
            return new(text.Text, text.Encoding, text.HasBom);
        }
        catch (TextLimitException) { throw new FileProcessingException(SR.Get("TooLarge")); }
        catch (DecoderFallbackException) { throw new FileProcessingException(SR.Get("InvalidText")); }
    }
}
