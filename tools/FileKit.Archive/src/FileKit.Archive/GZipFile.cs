using System.IO.Compression;

namespace FileKit.Archive;

public static class GZipFile
{
    public static async Task<long> DecompressAsync(Stream input, Stream output, ZipExtractionLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= ZipExtractionLimits.Default;
        long compressed = input.CanSeek ? input.Length - input.Position : 0;
        if (compressed <= 0) throw new InvalidArchiveException("GZip input must have a known size.");
        try
        {
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            byte[] buffer = new byte[81920]; long total = 0; int n;
            while ((n = await gzip.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > limits.MaxEntryUncompressedBytes || total > limits.MaxTotalUncompressedBytes || (total > 1024 * 1024 && (double)total / compressed > limits.MaxCompressionRatio)) throw new ArchiveLimitExceededException("GZip output exceeds a size or compression ratio limit.");
                await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            }
            return total;
        }
        catch (InvalidDataException ex) { throw new InvalidArchiveException("Invalid GZip stream.", ex); }
    }
}
