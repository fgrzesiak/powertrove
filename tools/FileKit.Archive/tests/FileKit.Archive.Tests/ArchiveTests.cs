using System.IO.Compression;
using System.Text;
using FileKit.Archive;

namespace FileKit.Archive.Tests;

public sealed class ArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filekit-archive-" + Guid.NewGuid().ToString("N"));

    public ArchiveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task CreateThenExtract_RoundTripsFilesAndFolders()
    {
        string a = Write("a.txt", "alpha");
        string folder = Path.Combine(_root, "folder");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "beta");

        string zipPath = Path.Combine(_root, "out.zip");
        ZipCreationResult created;
        await using (var output = File.Create(zipPath))
            created = await ZipBuilder.CreateAsync(output, new[] { a, folder });

        Assert.Equal(2, created.FilesAdded);
        Assert.Equal(3, created.DirectoriesAdded);

        string dest = Directory.CreateDirectory(Path.Combine(_root, "extract")).FullName;
        var result = await SafeZipExtractor.ExtractAsync(zipPath, dest);

        Assert.Equal(2, result.FilesExtracted);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(dest, "folder", "sub", "b.txt")));
        Assert.True(Directory.Exists(Path.Combine(dest, "folder", "empty")));
    }

    [Fact]
    public async Task Create_DuplicateTopLevelNames_AreDisambiguated()
    {
        string first = Path.Combine(_root, "one");
        string second = Path.Combine(_root, "two");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "same.txt"), "1");
        File.WriteAllText(Path.Combine(second, "same.txt"), "2");

        using var output = new MemoryStream();
        await ZipBuilder.CreateAsync(output, new[] { Path.Combine(first, "same.txt"), Path.Combine(second, "same.txt") });
        output.Position = 0;
        using var zip = new ZipArchive(output);

        Assert.Equal(new[] { "same (2).txt", "same.txt" }, zip.Entries.Select(e => e.FullName).OrderBy(n => n));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData("C:/abs.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("dir/CON.txt")]
    [InlineData("dir/name.")]
    [InlineData("dir/a:stream")]
    public async Task Extract_RejectsUnsafeEntryNames(string entryName)
    {
        string zipPath = CreateZip((entryName, "x"));
        string dest = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;

        await Assert.ThrowsAsync<UnsafeArchiveEntryException>(() => SafeZipExtractor.ExtractAsync(zipPath, dest));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task Extract_RejectsSymbolicLinkEntries()
    {
        string zipPath = Path.Combine(_root, "link.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("link");
            entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            using var writer = new StreamWriter(entry.Open());
            writer.Write("/etc/passwd");
        }

        string dest = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;
        await Assert.ThrowsAsync<UnsafeArchiveEntryException>(() => SafeZipExtractor.ExtractAsync(zipPath, dest));
    }

    [Fact]
    public async Task Extract_EnforcesEntryCountLimit()
    {
        string zipPath = CreateZip(("a.txt", "a"), ("b.txt", "b"), ("c.txt", "c"));
        string dest = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;

        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() =>
            SafeZipExtractor.ExtractAsync(zipPath, dest, new ZipExtractionLimits { MaxEntries = 2 }));
    }

    [Fact]
    public async Task Extract_EnforcesCompressionRatio()
    {
        // 8 MB of zeros compresses to a few KB: a miniature zip bomb.
        string zipPath = Path.Combine(_root, "bomb.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("zeros.bin", CompressionLevel.SmallestSize);
            using var stream = entry.Open();
            stream.Write(new byte[8 * 1024 * 1024]);
        }

        string dest = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;
        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() =>
            SafeZipExtractor.ExtractAsync(zipPath, dest, new ZipExtractionLimits { MaxCompressionRatio = 50 }));
        Assert.False(File.Exists(Path.Combine(dest, "zeros.bin")));
    }

    [Fact]
    public async Task Extract_EnforcesTotalSize()
    {
        string zipPath = CreateZip(("a.txt", new string('a', 4000)), ("b.txt", new string('b', 4000)));
        string dest = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;

        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() =>
            SafeZipExtractor.ExtractAsync(zipPath, dest, new ZipExtractionLimits { MaxTotalUncompressedBytes = 5000 }));
    }

    [Fact]
    public async Task Extract_NeverOverwritesExistingFiles()
    {
        string zipPath = CreateZip(("a.txt", "new"));
        string dest = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;
        File.WriteAllText(Path.Combine(dest, "a.txt"), "original");

        await Assert.ThrowsAsync<IOException>(() => SafeZipExtractor.ExtractAsync(zipPath, dest));
        Assert.Equal("original", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public async Task Extract_NotAZip_ThrowsInvalidArchive()
    {
        string path = Write("fake.zip", "this is not a zip archive at all");
        string dest = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;

        await Assert.ThrowsAsync<InvalidArchiveException>(() => SafeZipExtractor.ExtractAsync(path, dest));
    }

    [Fact]
    public async Task Extract_TruncatedAndCorruptedArchives_FailWithTypedErrors()
    {
        string zipPath = CreateZip(("a.txt", new string('x', 10_000)), ("b.txt", "hello"));
        byte[] original = File.ReadAllBytes(zipPath);
        var random = new Random(42);

        for (int i = 0; i < 200; i++)
        {
            byte[] mutated = (byte[])original.Clone();
            if (i % 2 == 0)
            {
                Array.Resize(ref mutated, random.Next(0, original.Length));
            }
            else
            {
                for (int k = 0; k < 8; k++) mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            }

            string dest = Directory.CreateDirectory(Path.Combine(_root, "fuzz" + i)).FullName;
            try
            {
                using var stream = new MemoryStream(mutated);
                await SafeZipExtractor.ExtractAsync(stream, dest);
            }
            catch (ArchiveException) { }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Extract_Cancellation_Throws()
    {
        string zipPath = CreateZip(("a.txt", "a"));
        string dest = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SafeZipExtractor.ExtractAsync(zipPath, dest, cancellationToken: cts.Token));
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private string CreateZip(params (string Name, string Content)[] entries)
    {
        string zipPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return zipPath;
    }
}
