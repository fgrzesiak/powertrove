using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PowerTrove.Abstractions.Manifest;
using Xunit;

namespace PowerTrove.ReleaseTools.Tests;

public sealed class PackBundleBuilderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PowerTrove-PackBundle-Test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Inventory_ExcludesSharedSdkAndTracksExactBytes()
    {
        Directory.CreateDirectory(_directory);
        byte[] data = [1, 2, 3, 4, 5];
        File.WriteAllBytes(Path.Combine(_directory, "Test.Composition.dll"), data);
        File.WriteAllBytes(Path.Combine(_directory, "PowerTrove.Abstractions.dll"), [7]);
        File.WriteAllText(Path.Combine(_directory, "Test.pdb"), "debug");
        var payload = PackBundleBuilder.Inventory(_directory, "Test.Composition.dll");
        var file = Assert.Single(payload.Files);
        Assert.Equal("payload/Test.Composition.dll", payload.EntryAssembly);
        Assert.Equal(5, payload.SizeBytes);
        Assert.Equal("payload/Test.Composition.dll", file.Path);
        Assert.Equal(5, file.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), file.Sha256);
    }

    [Fact]
    public void SignedBundle_ContainsOnlyInventoryAndVerifiableDerSignature()
    {
        Directory.CreateDirectory(_directory);
        byte[] data = [42, 10, 8, 99];
        File.WriteAllBytes(Path.Combine(_directory, "Test.Composition.dll"), data);
        var payload = PackBundleBuilder.Inventory(_directory, "Test.Composition.dll");
        var manifest = new ActionManifest("2.0", "powertrove.test", "1.0.0", "Test", "PowerTrove",
            "1", [], MinHostVersion: "0.1.0", Payload: payload, Capabilities: []);
        byte[] bytes = Encoding.UTF8.GetBytes(ActionPackManifest.ToJson(manifest));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string file = Path.Combine(_directory, "test.cbpack");
        PackBundleBuilder.WriteSignedBundle(file, _directory, manifest, bytes, key);
        using var zip = ZipFile.OpenRead(file);
        Assert.Equal(3, zip.Entries.Count);
        Assert.NotNull(zip.GetEntry("manifest.json"));
        Assert.NotNull(zip.GetEntry("payload/Test.Composition.dll"));
        using var signatureStream = zip.GetEntry("manifest.sig")!.Open();
        using var buffer = new MemoryStream();
        signatureStream.CopyTo(buffer);
        Assert.True(key.VerifyData(bytes, buffer.ToArray(), HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public async Task SignedMode_WithoutKey_FailsClosed()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "inputs", "a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PackBundleBuilder.BuildAsync(Path.Combine(_directory, "inputs"), Path.Combine(_directory, "out"), "", false));
        Assert.False(Directory.Exists(Path.Combine(_directory, "out", "packs")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (IOException) { }
    }
}
