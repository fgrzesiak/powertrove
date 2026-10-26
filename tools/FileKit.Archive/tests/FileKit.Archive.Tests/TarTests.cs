using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace FileKit.Archive.Tests;

public sealed class TarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filekit-tar-" + Guid.NewGuid().ToString("N"));
    public TarTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
    private string Destination() => Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
    private static MemoryStream Archive(string name, TarEntryType type = TarEntryType.RegularFile, string text = "hello", IEnumerable<KeyValuePair<string, string>>? attributes = null)
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = attributes is null ? new PaxTarEntry(type, name) : new PaxTarEntry(type, name, attributes);
            if (type == TarEntryType.RegularFile) entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = "target";
            writer.WriteEntry(entry);
        }
        stream.Position = 0; return stream;
    }
    [Theory]
    [InlineData("../escape")][InlineData("/absolute")][InlineData("C:/drive")][InlineData("a/../../escape")][InlineData("a/CON.txt")][InlineData("a/x:stream")]
    public async Task TraversalAndWindowsNamesAreRejected(string name)
    {
        using var archive = Archive(name); await Assert.ThrowsAsync<UnsafeArchiveEntryException>(() => SafeTarExtractor.ExtractAsync(archive, Destination()));
    }
    [Theory]
    [InlineData(TarEntryType.SymbolicLink)][InlineData(TarEntryType.HardLink)][InlineData(TarEntryType.BlockDevice)][InlineData(TarEntryType.CharacterDevice)][InlineData(TarEntryType.Fifo)]
    public async Task NonRegularTypesAreRejected(TarEntryType type)
    {
        using var archive = Archive("unsafe", type); await Assert.ThrowsAsync<UnsafeArchiveEntryException>(() => SafeTarExtractor.ExtractAsync(archive, Destination()));
    }
    [Fact]
    public async Task PaxTraversalIsRejected()
    {
        using var original = Archive("safe-file"); byte[] bytes = original.ToArray();
        int offset = bytes.AsSpan().IndexOf(" path=safe-file\n"u8);
        Assert.True(offset >= 0); "../escape"u8.CopyTo(bytes.AsSpan(offset + 6, 9));
        using var archive = new MemoryStream(bytes);
        await Assert.ThrowsAsync<UnsafeArchiveEntryException>(() => SafeTarExtractor.ExtractAsync(archive, Destination()));
    }
    [Fact]
    public async Task GlobalPaxHeadersAreRejected()
    {
        using var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, leaveOpen: true)) writer.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["path"] = "../escape" }));
        archive.Position = 0; await Assert.ThrowsAsync<UnsafeArchiveEntryException>(() => SafeTarExtractor.ExtractAsync(archive, Destination()));
    }
    [Fact]
    public async Task TarGzipRoundTripIncludesEmptyDirectory()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName; Directory.CreateDirectory(Path.Combine(folder, "empty")); File.WriteAllText(Path.Combine(folder, "hello.txt"), "hello");
        using var archive = new MemoryStream(); var created = await TarBuilder.CreateAsync(archive, new[] { folder }); Assert.Equal(1, created.FilesAdded);
        archive.Position = 0; string destination = Destination(); var extracted = await SafeTarExtractor.ExtractAsync(archive, destination, gzip: true);
        Assert.Equal(1, extracted.FilesExtracted); Assert.True(Directory.Exists(Path.Combine(destination, "source", "empty"))); Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "source", "hello.txt")));
    }
    [Fact]
    public async Task EntryAndByteLimitsRejectBeforeCommit()
    {
        using var archive = Archive("a", text: "12345");
        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => SafeTarExtractor.ExtractAsync(archive, Destination(), limits: new ZipExtractionLimits { MaxEntryUncompressedBytes = 4 }));
        using var entries = Archive("a"); await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => SafeTarExtractor.ExtractAsync(entries, Destination(), limits: new ZipExtractionLimits { MaxEntries = 0 }));
    }
    [Fact]
    public async Task GzipSizeAndRatioLimitsApplyToActualBytes()
    {
        using var compressed = new MemoryStream(); using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, true)) gzip.Write(new byte[2 * 1024 * 1024]); compressed.Position = 0;
        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => GZipFile.DecompressAsync(compressed, new MemoryStream(), new ZipExtractionLimits { MaxCompressionRatio = 10 }));
        compressed.Position = 0; await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => GZipFile.DecompressAsync(compressed, new MemoryStream(), new ZipExtractionLimits { MaxEntryUncompressedBytes = 10 }));
    }
    [Fact]
    public async Task CanceledExtractionDoesNotCreateFile()
    {
        using var archive = Archive("a"); string destination = Destination();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SafeTarExtractor.ExtractAsync(archive, destination, cancellationToken: new CancellationToken(true))); Assert.Empty(Directory.GetFileSystemEntries(destination));
    }
    [Fact]
    public async Task MutatedHeadersProduceOnlyTypedArchiveFailures()
    {
        using var original = Archive("a"); byte[] bytes = original.ToArray();
        foreach (int offset in new[] { 0, 124, 135, 148, 156, 512, 520 })
        {
            byte[] mutated = (byte[])bytes.Clone(); mutated[offset] ^= 0xFF; using var input = new MemoryStream(mutated);
            try { await SafeTarExtractor.ExtractAsync(input, Destination(), limits: new ZipExtractionLimits { MaxEntryUncompressedBytes = 1024, MaxTotalUncompressedBytes = 8192, MaxEntries = 20 }); }
            catch (ArchiveException) { }
        }
    }
}
