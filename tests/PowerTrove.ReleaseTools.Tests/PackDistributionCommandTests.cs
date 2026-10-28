using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace PowerTrove.ReleaseTools.Tests;

public sealed class PackDistributionCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PowerTrove-pack-configuration-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task ExplicitKeyGenerationIsOffline_AndRefusesRepositoryPathsAndOverwrites()
    {
        Directory.CreateDirectory(_root);
        string privatePath = Path.Combine(_root, "private.pem"), publicPath = Path.Combine(_root, "public.pem");
        await PackKeyCommand.GenerateAsync(privatePath, publicPath);
        using var signer = ECDsa.Create(); signer.ImportFromPem(await File.ReadAllTextAsync(privatePath));
        using var verifier = ECDsa.Create(); verifier.ImportFromPem(await File.ReadAllTextAsync(publicPath));
        byte[] content = "generated"u8.ToArray();
        Assert.True(verifier.VerifyData(content, signer.SignData(content, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
        await Assert.ThrowsAsync<IOException>(() => PackKeyCommand.GenerateAsync(privatePath, publicPath));
        Directory.CreateDirectory(Path.Combine(_root, "repo", ".git"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PackKeyCommand.GenerateAsync(Path.Combine(_root, "repo", "private.pem"), Path.Combine(_root, "repo", "public.pem")));
        Assert.False(File.Exists(Path.Combine(_root, "repo", "private.pem")));
    }
    [Fact]
    public async Task ConfigurationContainsOnlyPublicTrustMaterialAndExplicitChannel_AndNeverOverwrites()
    {
        Directory.CreateDirectory(_root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = Path.Combine(_root, "public.pem"), config = Path.Combine(_root, "configuration.json");
        await File.WriteAllTextAsync(pem, key.ExportSubjectPublicKeyInfoPem());
        await PackDistributionCommand.ExecuteAsync(pem, "https://owner.example/channel/", config);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(config));
        Assert.Equal("https://owner.example/channel/", json.RootElement.GetProperty("Channel").GetString());
        Assert.Equal(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), json.RootElement.GetProperty("TrustedKeys")[0].GetString());
        Assert.DoesNotContain("PRIVATE", await File.ReadAllTextAsync(config));
        await Assert.ThrowsAsync<IOException>(() => PackDistributionCommand.ExecuteAsync(pem, "https://owner.example/channel/", config));
    }
    [Theory]
    [InlineData("http://owner.example/channel/")]
    [InlineData("https://user@owner.example/channel/")]
    [InlineData("https://owner.example/channel/?token=test")]
    [InlineData("https://owner.example/channel")]
    public async Task NonCanonicalChannelsAreRejected(string channel)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PackDistributionCommand.ExecuteAsync("unused", channel, "unused"));
    }
    [Fact]
    public async Task PrivateKeysAreNeverCopiedIntoAReleaseConfiguration()
    {
        Directory.CreateDirectory(_root); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = Path.Combine(_root, "private.pem"), config = Path.Combine(_root, "configuration.json");
        await File.WriteAllTextAsync(pem, key.ExportECPrivateKeyPem());
        await Assert.ThrowsAsync<InvalidDataException>(() => PackDistributionCommand.ExecuteAsync(pem, "https://owner.example/channel/", config));
        Assert.False(File.Exists(config));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
