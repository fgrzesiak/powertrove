using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace PowerTrove.ReleaseTools.Tests;

public sealed class MsixPayloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PowerTrove-MsixPayload-" + Guid.NewGuid().ToString("N"));
    private string Payload => Path.Combine(_root, "payload");
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public MsixPayloadTests() => Directory.CreateDirectory(_root);

    private string CoreZip(params (string Name, string Content)[] extra)
    {
        string zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        var manifest = JsonSerializer.Serialize(new { GitRevision = Revision, Version = "0.3.2", ReleaseVersion = "0.3.2-alpha.1", Target = "win-x64" });
        foreach (var (name, content) in new[] { ("manifest.json", manifest), ("PowerTrove.exe", "generated executable fixture"),
            ("PowerTrove.PackHost.exe", "generated worker fixture"), ("de/PowerTrove.resources.dll", "generated satellite fixture") }.Concat(extra))
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(content);
        }
        return zip;
    }
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
    private Task<MsixPayload.InputEvidence> Extract(string zip, string? hash = null, string revision = Revision, string releaseVersion = "0.3.2-alpha.1", CancellationToken token = default) =>
        MsixPayload.ExtractAsync(zip, Payload, hash ?? Hash(zip), revision, "0.3.2", releaseVersion, token);
    private string Package()
    {
        string package = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".msix");
        ZipFile.CreateFromDirectory(Payload, package); return package;
    }

    [Fact]
    public async Task ExistingZip_IsPreservedByteForByte_WhilePackageMetadataCanBeAdded()
    {
        string zip = CoreZip();
        var input = await Extract(zip);
        Assert.Equal(Hash(zip), input.SourceZipSha256);
        Assert.Equal(4, input.Files.Count);
        File.WriteAllText(Path.Combine(Payload, "AppxManifest.xml"), "generated packaging metadata");
        string package = Package();
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update)) archive.CreateEntry("AppxBlockMap.xml");
        var final = await MsixPayload.VerifyAsync(package, Payload, input);
        Assert.Equal(5, final.Count);
        foreach (var original in input.Files) Assert.Contains(original, final);
    }

    [Fact]
    public async Task RepackedOrChangedCoreBinary_IsRejectedEvenWhenItsPackageCopyMatches()
    {
        var input = await Extract(CoreZip());
        File.AppendAllText(Path.Combine(Payload, "PowerTrove.exe"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => MsixPayload.VerifyAsync(Package(), Payload, input));
    }

    [Fact]
    public async Task ChangedPackageContentAndUnexpectedFiles_AreRejected()
    {
        var input = await Extract(CoreZip());
        string package = Package();
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            archive.GetEntry("PowerTrove.exe")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("PowerTrove.exe").Open()); writer.Write("modified archive content");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => MsixPayload.VerifyAsync(package, Payload, input));
        package = Package();
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update)) archive.CreateEntry("unexpected.dll");
        await Assert.ThrowsAsync<InvalidDataException>(() => MsixPayload.VerifyAsync(package, Payload, input));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/escape")]
    [InlineData("dir\\escape")]
    [InlineData("PowerTrove.EXE")]
    [InlineData("CON.txt")]
    [InlineData("dir/COM1")]
    public async Task UnsafeOrCaseCollidingArchivePaths_AreRefusedBeforeExtraction(string entry)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Extract(CoreZip((entry, "generated fixture"))));
        Assert.False(Directory.Exists(Payload));
    }

    [Fact]
    public async Task WrongHashSourceOrRelease_CannotBeUsedForAnotherCandidate()
    {
        string zip = CoreZip();
        await Assert.ThrowsAsync<InvalidDataException>(() => Extract(zip, new string('b', 64)));
        Assert.False(Directory.Exists(Payload));
        await Assert.ThrowsAsync<InvalidDataException>(() => Extract(zip, revision: new string('b', 40)));
    }

    [Fact]
    public async Task SymbolicLinkArchiveEntries_AreRejectedBeforeExtraction()
    {
        string zip = CoreZip();
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            var link = archive.CreateEntry("generated-link");
            link.ExternalAttributes = unchecked((int)0xa1ff0000);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Extract(zip));
        Assert.False(Directory.Exists(Payload));
    }

    [Fact]
    public async Task AlphaSequenceIsPartOfTheInputIdentity()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Extract(CoreZip(), releaseVersion: "0.3.2-alpha.2"));
    }

    [Fact]
    public async Task CancellationAndExistingOutput_NeverOverwriteAnEarlierCandidate()
    {
        string zip = CoreZip();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Extract(zip, token: new CancellationToken(true)));
        Assert.False(Directory.Exists(Payload));
        Directory.CreateDirectory(Payload);
        File.WriteAllText(Path.Combine(Payload, "generated.txt"), "preserve fixture");
        await Assert.ThrowsAsync<IOException>(() => Extract(zip));
        Assert.Equal("preserve fixture", File.ReadAllText(Path.Combine(Payload, "generated.txt")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
