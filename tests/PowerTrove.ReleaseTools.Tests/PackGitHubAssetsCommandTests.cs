using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerTrove.Abstractions.Manifest;
using Xunit;

namespace PowerTrove.ReleaseTools.Tests;

public sealed class PackGitHubAssetsCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PowerTrove-github-assets-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string KeyFile => Path.Combine(_root, "test-key.pem");
    public PackGitHubAssetsCommandTests()
    { Directory.CreateDirectory(_root); File.WriteAllText(KeyFile, _key.ExportPkcs8PrivateKeyPem()); }

    [Fact]
    public async Task ExportRetainsOlderCompatibleReleasesAndExactBundleBytes_WithSignedFlatCatalog()
    {
        string old = CreateInput("old", "1.0.0", "0.1.0", [1, 2, 3]);
        string first = Path.Combine(_root, "first");
        await PackGitHubAssetsCommand.ExecuteAsync(old, first, KeyFile);
        string current = CreateInput("new", "2.0.0", "2.0.0", [4, 5, 6]);
        string output = Path.Combine(_root, "merged");
        await PackGitHubAssetsCommand.ExecuteAsync(current, output, KeyFile, first);
        byte[] catalog = await File.ReadAllBytesAsync(Path.Combine(output, "catalog.json"));
        Assert.True(_key.VerifyData(catalog, await File.ReadAllBytesAsync(Path.Combine(output, "catalog.json.sig")),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        using var json = JsonDocument.Parse(catalog);
        Assert.Equal("2", json.RootElement.GetProperty("schemaVersion").GetString());
        var packs = json.RootElement.GetProperty("packs");
        Assert.Equal(2, packs.GetArrayLength());
        Assert.Equal("0.1.0", packs[0].GetProperty("manifest").GetProperty("minHostVersion").GetString());
        Assert.Equal("2.0.0", packs[1].GetProperty("manifest").GetProperty("minHostVersion").GetString());
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(old, "powertrove.fixture", "1.0.0.cbpack")),
            await File.ReadAllBytesAsync(Path.Combine(output, "powertrove.fixture-1.0.0.cbpack")));
        Assert.Equal(4, Directory.GetFiles(output).Length);
        Assert.Empty(Directory.GetDirectories(output));
        await Assert.ThrowsAsync<IOException>(() => PackGitHubAssetsCommand.ExecuteAsync(current, output, KeyFile));
    }

    [Fact]
    public async Task ReusingPublishedVersionWithDifferentBytesIsRejected_WithoutOutput()
    {
        string old = CreateInput("old", "1.0.0", "0.1.0", [1]);
        string changed = CreateInput("changed", "1.0.0", "0.1.0", [2]);
        string output = Path.Combine(_root, "refused");
        await Assert.ThrowsAsync<InvalidDataException>(() => PackGitHubAssetsCommand.ExecuteAsync(changed, output, KeyFile, old));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task RebuildingIdenticalPayloadRetainsOriginalRandomizedSignatureAndBundleBytes()
    {
        string old = CreateInput("old", "1.0.0", "0.1.0", [1, 2, 3]);
        string rebuilt = CreateInput("rebuilt", "1.0.0", "0.1.0", [1, 2, 3]);
        string output = Path.Combine(_root, "merged");
        await PackGitHubAssetsCommand.ExecuteAsync(rebuilt, output, KeyFile, old);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(old, "powertrove.fixture", "1.0.0.cbpack")),
            await File.ReadAllBytesAsync(Path.Combine(output, "powertrove.fixture-1.0.0.cbpack")));
    }

    [Fact]
    public async Task CatalogSignatureAndDeclaredBundleLengthAreVerifiedBeforeExport()
    {
        string input = CreateInput("input", "1.0.0", "0.1.0", [1]);
        string output = Path.Combine(_root, "refused");
        byte[] originalSignature = await File.ReadAllBytesAsync(Path.Combine(input, "catalog.json.sig"));
        byte[] altered = (byte[])originalSignature.Clone(); altered[^1] ^= 1;
        await File.WriteAllBytesAsync(Path.Combine(input, "catalog.json.sig"), altered);
        await Assert.ThrowsAsync<InvalidDataException>(() => PackGitHubAssetsCommand.ExecuteAsync(input, output, KeyFile));
        await File.WriteAllBytesAsync(Path.Combine(input, "catalog.json.sig"), originalSignature);
        await File.AppendAllTextAsync(Path.Combine(input, "powertrove.fixture", "1.0.0.cbpack"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => PackGitHubAssetsCommand.ExecuteAsync(input, output, KeyFile));
        Assert.False(Directory.Exists(output));
    }

    private string CreateInput(string name, string version, string minimum, byte[] payload)
    {
        string published = Path.Combine(_root, name + "-published"); Directory.CreateDirectory(published);
        File.WriteAllBytes(Path.Combine(published, "Fixture.Composition.dll"), payload);
        var inventory = PackBundleBuilder.Inventory(published, "Fixture.Composition.dll");
        var manifest = new ActionManifest("2.0", "powertrove.fixture", version, "Generated fixture", "PowerTrove tests", "1",
            [new ManifestAction("powertrove.fixture.copy", "Generated action", ["files"],
                Selection: new ManifestSelection(["any"], 1, null, null, null, false), ChangesFiles: true)], MinHostVersion: minimum, Payload: inventory, Capabilities: []);
        string root = Path.Combine(_root, name); Directory.CreateDirectory(Path.Combine(root, manifest.Id));
        string bundle = Path.Combine(root, manifest.Id, version + ".cbpack");
        string manifestJson = ActionPackManifest.ToJson(manifest);
        PackBundleBuilder.WriteSignedBundle(bundle, published, manifest, Encoding.UTF8.GetBytes(manifestJson), _key);
        byte[] catalog = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "1", packs = new[]
            { new { manifest = JsonSerializer.Deserialize<JsonElement>(manifestJson), downloadBytes = new FileInfo(bundle).Length } } });
        File.WriteAllBytes(Path.Combine(root, "catalog.json"), catalog);
        File.WriteAllBytes(Path.Combine(root, "catalog.json.sig"), _key.SignData(catalog, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        return root;
    }
    public void Dispose() { _key.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
