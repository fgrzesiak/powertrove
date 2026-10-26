using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Files;

namespace PowerTrove.Pack.Files.Tests;

public sealed class FileToolsTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static FileActionServices Services() => FileActionServices.CreatePhysical(new FileSignatureVerifier());

    [Fact]
    public async Task Zip_SingleFolder_IsNamedAfterFolder_AndRoundTrips()
    {
        string folder = Directory.CreateDirectory(_dir.File("Holiday")).FullName;
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "beach");

        var receipt = await new CompressToZipAction(Services()).ExecuteAsync(Snapshots.ForPaths(folder), new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);
        string zip = Assert.Single(receipt.OutputPaths);
        Assert.Equal(_dir.File("Holiday.zip"), zip);
        using var archive = ZipFile.OpenRead(zip);
        Assert.Contains(archive.Entries, e => e.FullName == "Holiday/notes.txt");
    }

    [Fact]
    public async Task Zip_MultipleItems_UsesParentFolderName()
    {
        string a = _dir.Write("a.txt", "a");
        string b = _dir.Write("b.txt", "b");

        var receipt = await new CompressToZipAction(Services()).ExecuteAsync(Snapshots.ForPaths(a, b), Snapshots.Parameters(("Compression", "store")), null, CancellationToken.None);

        Assert.Equal(_dir.File(Path.GetFileName(_dir.Path) + ".zip"), Assert.Single(receipt.OutputPaths));
    }

    [Fact]
    public async Task Unzip_ExtractsIntoNewFolder_AndRefusesUnsafeArchives()
    {
        string good = _dir.File("good.zip");
        using (var zip = ZipFile.Open(good, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("docs/readme.txt").Open());
            writer.Write("hi");
        }
        string evil = _dir.File("evil.zip");
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open());
            writer.Write("nope");
        }

        var receipt = await new ExtractZipAction(Services()).ExecuteAsync(Snapshots.ForPaths(good, evil), new EmptyParameters(), null, CancellationToken.None);

        Assert.Equal(_dir.File("good"), Assert.Single(receipt.OutputPaths));
        Assert.Equal("hi", File.ReadAllText(Path.Combine(_dir.File("good"), "docs", "readme.txt")));
        var failure = Assert.Single(receipt.Failures);
        Assert.Equal("The archive contains an unsafe entry and cannot be extracted.", failure.ErrorMessage);
        Assert.DoesNotContain("escape.txt", failure.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir.Path)!, "escape.txt")));
        Assert.DoesNotContain(Directory.GetDirectories(_dir.Path), d => Path.GetFileName(d).StartsWith(".~cb", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unzip_FakeZipByExtension_IsRejectedByContentCheck()
    {
        string fake = _dir.Write("fake.zip", "plain text pretending to be a zip");

        var receipt = await new ExtractZipAction(Services()).ExecuteAsync(Snapshots.ForPaths(fake), new EmptyParameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Contains("not a recognized", Assert.Single(receipt.Failures).ErrorMessage);
    }

    [Fact]
    public async Task Checksum_WritesSha256SumCompatibleFile()
    {
        string a = _dir.Write("a.bin", "alpha");
        string b = _dir.Write("b.bin", "beta");

        var receipt = await new ChecksumAction(Services()).ExecuteAsync(Snapshots.ForPaths(b, a), new EmptyParameters(), null, CancellationToken.None);

        string file = Assert.Single(receipt.OutputPaths);
        Assert.Equal(_dir.File("checksums.sha256"), file);
        string expected =
            $"{Hex(SHA256.HashData(Encoding.UTF8.GetBytes("alpha")))} *a.bin\n" +
            $"{Hex(SHA256.HashData(Encoding.UTF8.GetBytes("beta")))} *b.bin\n";
        Assert.Equal(expected, File.ReadAllText(file));
    }

    [Fact]
    public async Task Checksum_SingleFile_Sha512_IsNamedAfterFile()
    {
        string a = _dir.Write("setup.exe", "binary");

        var receipt = await new ChecksumAction(Services()).ExecuteAsync(Snapshots.ForPaths(a), Snapshots.Parameters(("Algorithm", "sha512")), null, CancellationToken.None);

        Assert.Equal(_dir.File("setup.exe.sha512"), Assert.Single(receipt.OutputPaths));
        Assert.StartsWith(Hex(SHA512.HashData(Encoding.UTF8.GetBytes("binary"))), File.ReadAllText(receipt.OutputPaths[0]));
    }

    [Fact]
    public void Pack_ExposesAllActions()
    {
        var pack = new FilesPack(Services());
        Assert.Equal(new[] { "contextbar.files.count", "contextbar.files.rename", "contextbar.files.add-to-zip", "contextbar.files.compare-text", "contextbar.files.sort-by-type", "contextbar.files.zip", "contextbar.files.unzip", "contextbar.files.checksum", "contextbar.files.tar", "contextbar.files.untar", "contextbar.files.find-duplicates", "contextbar.files.folder-report", "contextbar.files.copy-paths", "contextbar.files.copy-names", "contextbar.files.split", "contextbar.files.join", "contextbar.files.base64-encode", "contextbar.files.base64-decode", "contextbar.files.verify-checksums" },
            pack.Actions.Select(a => a.Descriptor.Id));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

public sealed class ZipContractTests : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _dir = new();
    protected override IAction CreateAction() => new CompressToZipAction(FileActionServices.CreatePhysical(new FileSignatureVerifier()));
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(_dir.Write("a.txt", "x"));
    public void Dispose() => _dir.Dispose();
}
