using System.Runtime.Loader;
using System.Security.Cryptography;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Abstractions.Packs;
using PowerTrove.PackHost.TestPack;

namespace PowerTrove.PackHost.Tests;

public sealed class PackLoaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task VerifiedTestPackLoadsInItsOwnContextWithTheSharedSdkContract()
    {
        var (directory, manifest) = WriteFixture();
        using var first = await LoadedPack.LoadAsync(directory, manifest, _temp.File("work1"), new Version(0, 1, 0));
        using var second = await LoadedPack.LoadAsync(directory, manifest, _temp.File("work2"), new Version(0, 1, 0));
        var firstContext = AssemblyLoadContext.GetLoadContext(first.Factory.GetType().Assembly);
        Assert.NotSame(AssemblyLoadContext.Default, firstContext);
        Assert.NotSame(firstContext, AssemblyLoadContext.GetLoadContext(second.Factory.GetType().Assembly));
        Assert.True(firstContext!.IsCollectible);
        Assert.IsAssignableFrom<IPackFactory>(first.Factory);
        var pack = first.Factory.Create(new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier())));
        Assert.Equal(first.Manifest.Actions.Select(a => a.Id), pack.Actions.Select(a => a.Descriptor.Id));
    }

    [Fact]
    public async Task SameSizeHashMutationIsRejectedBeforeAssemblyLoadingAndShadowIsCleaned()
    {
        var (directory, manifest) = WriteFixture();
        string assembly = Path.Combine(directory, "PowerTrove.PackHost.TestPack.dll");
        byte[] bytes = File.ReadAllBytes(assembly); bytes[^1] ^= 1; File.WriteAllBytes(assembly, bytes);
        string work = _temp.File("work");
        await Assert.ThrowsAsync<InvalidDataException>(() => LoadedPack.LoadAsync(directory, manifest, work, new Version(0, 1, 0)));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(work, "loads")));
    }

    [Fact]
    public async Task HostVersionAndFactoryIdentityMustMatchTheVerifiedManifest()
    {
        var (directory, manifest) = WriteFixture(minimum: new Version(99, 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => LoadedPack.LoadAsync(directory, manifest, _temp.File("work"), new Version(0, 1, 0)));
        var original = ActionPackManifest.Read(File.ReadAllText(manifest));
        File.WriteAllText(manifest, ActionPackManifest.ToJson(original with { MinHostVersion = "0.1.0", Id = "powertrove.wrong" }));
        await Assert.ThrowsAsync<InvalidDataException>(() => LoadedPack.LoadAsync(directory, manifest, _temp.File("work"), new Version(0, 1, 0)));
    }

    [Fact]
    public async Task ManifestChangedAfterHostAuthorizationIsRejectedEvenWhenItsPayloadHashesAreValid()
    {
        var (directory, manifest) = WriteFixture();
        string pinned = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)));
        var parsed = ActionPackManifest.Read(File.ReadAllText(manifest));
        File.WriteAllText(manifest, ActionPackManifest.ToJson(parsed with { DisplayName = "Changed metadata" }));
        await Assert.ThrowsAsync<InvalidDataException>(() => LoadedPack.LoadAsync(directory, manifest, _temp.File("work"),
            new Version(0, 1, 0), trustedManifestHashes: new HashSet<string> { pinned }));
    }

    [Fact]
    public async Task OversizedManifestIsRejectedWithoutReadingItIntoAnUnboundedString()
    {
        var (directory, manifest) = WriteFixture();
        using (var file = new FileStream(manifest, FileMode.Open, FileAccess.Write)) file.SetLength(ActionPackManifest.MaximumJsonBytes + 1L);
        await Assert.ThrowsAsync<InvalidDataException>(() => LoadedPack.LoadAsync(directory, manifest, _temp.File("work"), new Version(0, 1, 0)));
    }

    private (string Directory, string Manifest) WriteFixture(Version? minimum = null)
    {
        string directory = Directory.CreateDirectory(_temp.File("pack")).FullName;
        byte[] bytes = File.ReadAllBytes(typeof(TinyPackFactory).Assembly.Location);
        const string filename = "PowerTrove.PackHost.TestPack.dll";
        File.WriteAllBytes(Path.Combine(directory, filename), bytes);
        var payload = new ManifestPayload(filename, bytes.LongLength,
            [new ManifestPayloadFile(filename, Convert.ToHexString(SHA256.HashData(bytes)), bytes.LongLength)]);
        var factory = new TinyPackFactory();
        var pack = factory.Create(new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier())));
        string manifest = Path.Combine(directory, "manifest.json");
        File.WriteAllText(manifest, ActionPackManifest.ToJson(ActionPackManifest.Create(pack, minHostVersion: minimum, payload: payload)));
        return (directory, manifest);
    }
}
