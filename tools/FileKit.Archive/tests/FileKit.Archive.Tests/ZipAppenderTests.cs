using System.IO.Compression;

namespace FileKit.Archive.Tests;

public sealed class ZipAppenderTests
{
    [Fact]
    public async Task OversizedAndBombSourceAreRefused()
    {
        using var source = new MemoryStream();
        using (var zip = new ZipArchive(source, ZipArchiveMode.Create, true))
        { using var write = new StreamWriter(zip.CreateEntry("large.txt").Open()); write.Write(new string('x', 2 * 1024 * 1024)); }
        source.Position = 0; using var output = new MemoryStream();
        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => ZipAppender.AddAsync(source, output, [], new ZipExtractionLimits { MaxEntryUncompressedBytes = 1024 }));
        source.Position = 0;
        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => ZipAppender.AddAsync(source, output, [], new ZipExtractionLimits { MaxCompressionRatio = 2 }));
    }
    [Theory]
    [InlineData("/absolute")][InlineData("../outside")][InlineData("a/../../outside")][InlineData("NUL.txt")][InlineData("C:\\outside")]
    public async Task MutatedUnsafeNamesFailBeforeWriting(string name)
    {
        using var source = new MemoryStream(); using (var zip = new ZipArchive(source, ZipArchiveMode.Create, true)) zip.CreateEntry(name);
        source.Position = 0; using var output = new MemoryStream();
        await Assert.ThrowsAsync<UnsafeArchiveEntryException>(() => ZipAppender.AddAsync(source, output, [])); Assert.Equal(0, output.Length);
    }
}
