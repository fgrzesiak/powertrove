using System.Formats.Tar;
using System.IO.Compression;

namespace FileKit.Archive;

public static class SafeTarExtractor
{
    public static async Task<ZipExtractionResult> ExtractAsync(string archivePath, string destinationDirectory,
        bool gzip = false, ZipExtractionLimits? limits = null, CancellationToken cancellationToken = default)
    {
        await using var input = File.OpenRead(archivePath);
        return await ExtractAsync(input, destinationDirectory, gzip, limits, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ZipExtractionResult> ExtractAsync(Stream input, string destinationDirectory,
        bool gzip = false, ZipExtractionLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= ZipExtractionLimits.Default;
        string root = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The destination directory does not exist.");
        CheckParents(root, root);
        long compressed = input.CanSeek ? input.Length - input.Position : 0;
        if (gzip && compressed <= 0) throw new InvalidArchiveException("Compressed TAR input must have a known size.");
        try
        {
            using var uncompressed = gzip ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true) : null;
            using var guarded = new TarGuardStream(uncompressed ?? input, limits, gzip ? compressed : 0);
            using var reader = new TarReader(guarded, leaveOpen: true);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int files = 0, directories = 0, entries = 0; long total = 0;
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is TarEntry entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > limits.MaxEntries) throw new ArchiveLimitExceededException("Too many archive entries.");
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory)) throw new UnsafeArchiveEntryException("Unsupported TAR entry type.");
                if (entry is PaxTarEntry pax)
                {
                    if (pax.ExtendedAttributes.ContainsKey("linkpath")) throw new UnsafeArchiveEntryException("PAX link targets are not accepted.");
                    if (pax.ExtendedAttributes.TryGetValue("path", out string? path)) ArchivePathRules.NormalizeEntryName(path, limits);
                }
                string relative = ArchivePathRules.NormalizeEntryName(entry.Name, limits);
                if (relative.Length == 0) continue;
                string target = Path.GetFullPath(Path.Combine(root, relative));
                if (!target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UnsafeArchiveEntryException("Entry escapes the destination.");
                if (!seen.Add(target)) throw new UnsafeArchiveEntryException("Duplicate TAR entry.");
                CheckParents(root, target);
                if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); directories++; continue; }
                if (entry.Length > limits.MaxEntryUncompressedBytes) throw new ArchiveLimitExceededException("TAR entry exceeds the size limit.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                bool created = false;
                try
                {
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); created = true;
                    byte[] buffer = new byte[81920]; long written = 0; int n;
                    if (entry.DataStream is not null)
                        while ((n = await entry.DataStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            written += n; total += n;
                            if (written > entry.Length || written > limits.MaxEntryUncompressedBytes || total > limits.MaxTotalUncompressedBytes) throw new ArchiveLimitExceededException("TAR output exceeds the size limit.");
                            await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                        }
                    if (written != entry.Length) throw new InvalidArchiveException("Truncated TAR entry.");
                }
                catch { if (created) { try { File.Delete(target); } catch (IOException) { } } throw; }
                files++;
            }
            // Drain permitted zero padding, validating the GZip trailer instead of accepting a
            // successful TAR end marker with a corrupt compressed tail or concatenated archive.
            byte[] tail = new byte[81920]; long padding = 0; int extra;
            Stream remainder = uncompressed ?? input;
            while ((extra = await remainder.ReadAsync(tail, cancellationToken).ConfigureAwait(false)) > 0)
            {
                padding += extra;
                if (padding > 1024 * 1024 || tail.AsSpan(0, extra).IndexOfAnyExcept((byte)0) >= 0) throw new InvalidArchiveException("Unexpected TAR trailing data.");
            }
            return new(files, directories, total);
        }
        catch (InvalidDataException ex) { throw new InvalidArchiveException("The TAR/GZip archive is invalid.", ex); }
        catch (EndOfStreamException ex) { throw new InvalidArchiveException("The TAR/GZip archive is truncated.", ex); }
        catch (FormatException ex) { throw new InvalidArchiveException("Invalid TAR numeric metadata.", ex); }
        catch (OverflowException ex) { throw new InvalidArchiveException("TAR metadata exceeds supported limits.", ex); }
    }

    private static void CheckParents(string root, string target)
    {
        string? current = target;
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new UnsafeArchiveEntryException("Reparse points are not accepted in the destination.");
            if (current == root) break;
            current = Path.GetDirectoryName(current);
        }
    }
}

/// <summary>Bounds TAR headers and PAX data before TarReader may allocate from untrusted metadata.</summary>
internal sealed class TarGuardStream(Stream source, ZipExtractionLimits limits, long compressedBytes) : Stream
{
    private readonly byte[] _header = new byte[512];
    private int _headerPosition = 512;
    private long _remaining, _total; private int _entries; private bool _ended;
    private MemoryStream? _pax;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return 0;
        if (_headerPosition == 512 && _remaining == 0)
        {
            if (_ended) return 0;
            int read = await source.ReadAtLeastAsync(_header, 512, false, ct).ConfigureAwait(false);
            if (read != 512) throw new InvalidArchiveException("Truncated TAR header.");
            _ended = _header.All(b => b == 0);
            if (!_ended)
            {
                if (++_entries > limits.MaxEntries * 2L) throw new ArchiveLimitExceededException("Too many TAR headers.");
                byte type = _header[156];
                if (type is not (0 or (byte)'0' or (byte)'5' or (byte)'x')) throw new UnsafeArchiveEntryException("Unsupported TAR entry type.");
                string size = System.Text.Encoding.ASCII.GetString(_header, 124, 12).Trim('\0', ' ');
                long length = size.Length == 0 ? 0 : Convert.ToInt64(size, 8);
                if (length < 0 || length > limits.MaxEntryUncompressedBytes) throw new ArchiveLimitExceededException("TAR entry exceeds the size limit.");
                if (type == (byte)'x') { if (length > 1024 * 1024) throw new ArchiveLimitExceededException("PAX metadata is too large."); _pax = new(); }
                _remaining = checked((length + 511) / 512 * 512);
            }
            _headerPosition = 0;
        }
        int n;
        if (_headerPosition < 512)
        {
            n = Math.Min(buffer.Length, 512 - _headerPosition); _header.AsMemory(_headerPosition, n).CopyTo(buffer); _headerPosition += n;
        }
        else
        {
            n = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], ct).ConfigureAwait(false);
            if (n == 0) throw new InvalidArchiveException("Truncated TAR entry.");
            _remaining -= n;
            if (_pax is not null)
            {
                _pax.Write(buffer.Span[..n]);
                if (_remaining == 0)
                {
                    ValidatePax(_pax.ToArray());
                    _pax.Dispose(); _pax = null;
                }
            }
        }
        _total += n;
        long allowance = Math.Min(limits.MaxEntries, 1000000) * 2048L;
        if (_total > limits.MaxTotalUncompressedBytes + allowance) throw new ArchiveLimitExceededException("TAR stream exceeds the size limit.");
        if (compressedBytes > 0 && _total > 1024 * 1024 && (double)_total / compressedBytes > limits.MaxCompressionRatio) throw new ArchiveLimitExceededException("Suspicious GZip compression ratio.");
        return n;
    }
    private void ValidatePax(byte[] bytes)
    {
        int position = 0, records = 0;
        while (position < bytes.Length && bytes[position] != 0)
        {
            if (++records > 4096) throw new ArchiveLimitExceededException("Too many PAX attributes.");
            int space = Array.IndexOf(bytes, (byte)' ', position);
            if (space < 0 || space - position > 12 || !int.TryParse(System.Text.Encoding.ASCII.GetString(bytes, position, space - position), out int length) || length <= space - position + 2 || length > bytes.Length - position || bytes[position + length - 1] != (byte)'\n') throw new InvalidArchiveException("Malformed PAX attribute.");
            string record;
            try { record = new System.Text.UTF8Encoding(false, true).GetString(bytes, space + 1, position + length - space - 2); }
            catch (System.Text.DecoderFallbackException ex) { throw new InvalidArchiveException("Malformed PAX text.", ex); }
            int equals = record.IndexOf('='); if (equals < 1) throw new InvalidArchiveException("Malformed PAX attribute.");
            string key = record[..equals];
            if (key == "linkpath" || key.Contains("sparse", StringComparison.OrdinalIgnoreCase)) throw new UnsafeArchiveEntryException("Unsupported PAX attribute.");
            if (key == "path") ArchivePathRules.NormalizeEntryName(record[(equals + 1)..], limits);
            position += length;
        }
    }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    protected override void Dispose(bool disposing) { if (disposing) _pax?.Dispose(); base.Dispose(disposing); }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
