using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Application.Packs;
using PowerTrove.Core;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Application.Tests;

public sealed class PackInstallerTests
{
    [Fact]
    public async Task SignedBundleInstallsWithoutLoadingCodeAndPreviousVersionSurvivesUntilSuccessfulStart()
    {
        using var fixture = new Fixture();
        var first = await fixture.Installer.InstallAsync(fixture.Bundle("0.1.0"));
        Assert.Equal("generated payload", File.ReadAllText(Path.Combine(first.Directory, "payload/fixture.dll")));
        Assert.Equal("0.1.0", Assert.Single(await fixture.Installer.GetInstalledAsync()).Manifest.Version);
        var second = await fixture.Installer.InstallAsync(fixture.Bundle("0.2.0"));
        Assert.True(Directory.Exists(first.Directory));
        using (var active = JsonDocument.Parse(File.ReadAllBytes(fixture.ActivePath)))
        {
            Assert.Equal("0.2.0", active.RootElement.GetProperty("Version").GetString());
            Assert.Equal("0.1.0", active.RootElement.GetProperty("PreviousVersion").GetString());
            Assert.False(active.RootElement.GetProperty("Confirmed").GetBoolean());
        }
        await fixture.Installer.ConfirmStartedAsync("powertrove.fixture", "0.1.0");
        Assert.True(Directory.Exists(first.Directory));
        await fixture.Installer.ConfirmStartedAsync("powertrove.fixture", "0.2.0");
        Assert.False(Directory.Exists(first.Directory));
        Assert.True(Directory.Exists(second.Directory));
        fixture.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task FailedAtomicActivationPreservesTheExactOldPointerAndRemovesTheNewVersion()
    {
        using var fixture = new Fixture();
        var first = await fixture.Installer.InstallAsync(fixture.Bundle("0.1.0"));
        byte[] previous = File.ReadAllBytes(fixture.ActivePath);
        fixture.Files.FailActivation = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Installer.InstallAsync(fixture.Bundle("0.2.0")));
        Assert.Equal(previous, File.ReadAllBytes(fixture.ActivePath));
        Assert.True(Directory.Exists(first.Directory));
        Assert.False(Directory.Exists(fixture.Temp.File("user/packs/powertrove.fixture/0.2.0")));
        fixture.AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("payload")]
    [InlineData("extra")]
    [InlineData("traversal")]
    [InlineData("signed-traversal")]
    [InlineData("host")]
    [InlineData("duplicate")]
    [InlineData("directory")]
    [InlineData("symlink")]
    [InlineData("metadata-limit")]
    [InlineData("missing")]
    [InlineData("id")]
    public async Task InvalidBundleNeverActivatesAndCleansAnyStaging(string mutation)
    {
        using var fixture = new Fixture();
        string bundle = fixture.Bundle("0.1.0", mutation);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Installer.InstallAsync(bundle));
        Assert.True(failure is InvalidDataException or JsonException);
        Assert.Empty(await fixture.Installer.GetInstalledAsync());
        Assert.False(File.Exists(fixture.ActivePath));
        Assert.False(File.Exists(fixture.Temp.File("escape.dll")));
        fixture.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ProductionPlaceholderRejectsAGenuineTestSignature()
    {
        using var fixture = new Fixture();
        var production = new PackInstaller(fixture.Temp.File("production"), new Version(0, 1, 0), (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidDataException>(() => production.InstallAsync(fixture.Bundle("0.1.0")));
        Assert.Empty(await production.GetInstalledAsync());
    }

    [Fact]
    public void SignatureVerifierRejectsOtherCurvesAndMalformedDer()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var verifier = new PackSignatureVerifier([Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())]);
        byte[] content = "generated"u8.ToArray();
        Assert.False(verifier.Verify(content, key.SignData(content, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
        Assert.False(verifier.Verify(content, [1, 2, 3]));
    }

    [Fact]
    public async Task UninstallWaitsForWorkersBeforeRemovingEveryVersion()
    {
        using var fixture = new Fixture();
        await fixture.Installer.InstallAsync(fixture.Bundle("0.1.0"));
        await fixture.Installer.InstallAsync(fixture.Bundle("0.2.0"));
        fixture.Files.UninstallObserved = () => Assert.True(fixture.StopCompleted);
        fixture.StopCompleted = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.StopHook = (_, token) => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        var uninstall = fixture.Installer.UninstallAsync("powertrove.fixture");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Directory.Exists(fixture.Temp.File("user/packs/powertrove.fixture")));
        Assert.False(uninstall.IsCompleted);
        release.TrySetResult();
        await uninstall;
        Assert.True(fixture.StopCompleted);
        Assert.False(Directory.Exists(fixture.Temp.File("user/packs/powertrove.fixture")));
    }

    [Fact]
    public async Task CancellationDuringExtractionPreservesTheOldActivationAndRemovesPartialPayloads()
    {
        using var fixture = new Fixture();
        await fixture.Installer.InstallAsync(fixture.Bundle("0.1.0"));
        byte[] old = File.ReadAllBytes(fixture.ActivePath);
        using var cancel = new CancellationTokenSource();
        fixture.Files.CancelPayload = cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Installer.InstallAsync(fixture.Bundle("0.2.0"), cancel.Token));
        Assert.Equal(old, File.ReadAllBytes(fixture.ActivePath));
        fixture.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task RuntimeRegistersOnlyRemoteActionsAndUninstallRemovesThemWithoutLaunchingAWorker()
    {
        using var fixture = new Fixture();
        await fixture.Installer.InstallAsync(fixture.Bundle("0.1.0"));
        var factory = new RejectProcessFactory();
        await using var supervisor = new PackHostSupervisor(factory, fixture.Temp.File("user"));
        var registry = new ActionRegistry();
        var runtime = new InstalledPackRuntime(fixture.Installer, supervisor, registry, [], new EmptyCatalog());
        await runtime.ReloadAsync();
        Assert.IsType<RemoteAction>(registry.GetAction("powertrove.fixture.copy"));
        Assert.Equal(0, factory.Starts);
        await fixture.Installer.UninstallAsync("powertrove.fixture");
        await runtime.ReloadAsync();
        Assert.Empty(registry.GetAllActions());
    }

    [Fact]
    public async Task LocalManifestTamperingAndDowngradesAreRejected()
    {
        using var fixture = new Fixture();
        var pack = await fixture.Installer.InstallAsync(fixture.Bundle("0.2.0"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Bundle("0.1.0")));
        string manifest = Path.Combine(pack.Directory, "manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("Fixture tools", "Modified tools", StringComparison.Ordinal));
        Assert.Empty(await fixture.Installer.GetInstalledAsync());
    }

    private sealed class EmptyCatalog : IActionCatalog
    { public IReadOnlyList<CatalogEntry> Entries => []; }
    private sealed class RejectProcessFactory : IPackWorkerProcessFactory
    {
        public int Starts { get; private set; }
        public Task<IPackWorkerProcess> StartAsync(PackWorkerGroup group, string workDirectory, CancellationToken token)
        { Starts++; throw new InvalidOperationException("Metadata registration must never start a worker."); }
    }
    private sealed class Fixture : IDisposable
    {
        public TempDirectory Temp { get; } = new();
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public FaultFileSystem Files { get; } = new();
        public bool StopCompleted;
        public Func<string, CancellationToken, Task>? StopHook;
        public PackInstaller Installer { get; }
        public string ActivePath => Temp.File("user/packs/powertrove.fixture/active.json");
        public Fixture() => Installer = new PackInstaller(Temp.File("user"), new Version(0, 1, 0), async (id, token) =>
        { if (StopHook is { } hook) await hook(id, token); StopCompleted = true; }, Files, new PackSignatureVerifier([Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo())]));
        public string Bundle(string version, string? mutation = null)
        {
            byte[] payload = "generated payload"u8.ToArray();
            const string payloadName = "payload/fixture.dll";
            var manifest = new ActionManifest("2.0", "powertrove.fixture", version, "Fixture tools", "PowerTrove tests", "1",
                [new ManifestAction("powertrove.fixture.copy", "Fixture copy", ["files"], RequiresPreview: true,
                    Selection: new ManifestSelection(["text"], 1, null, null, null, false), Extensions: [".txt"], ChangesFiles: true)],
                MinHostVersion: mutation == "host" ? "99.0.0" : "0.1.0",
                Payload: new ManifestPayload(payloadName, payload.LongLength,
                    [new ManifestPayloadFile(payloadName, Convert.ToHexString(SHA256.HashData(payload)), payload.LongLength)]), Capabilities: []);
            string json = ActionPackManifest.ToJson(manifest);
            if (mutation == "signed-traversal") json = json.Replace(payloadName, "../escape.dll", StringComparison.Ordinal);
            if (mutation == "id") json = json.Replace("powertrove.fixture\"", "../escape\"", StringComparison.Ordinal);
            byte[] raw = Encoding.UTF8.GetBytes(json);
            byte[] signature = _key.SignData(raw, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            if (mutation == "manifest") raw = Encoding.UTF8.GetBytes(json.Replace("Fixture tools", "Changed tools", StringComparison.Ordinal));
            if (mutation == "payload") payload[0] ^= 1;
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(archive, "manifest.json", raw); Write(archive, "manifest.sig", signature);
                var payloadEntry = mutation == "missing" ? null : Write(archive, payloadName, payload);
                if (mutation == "extra") Write(archive, "extra.dll", [1]);
                if (mutation == "traversal") Write(archive, "../escape.dll", [1]);
                if (mutation == "duplicate") Write(archive, "MANIFEST.JSON", raw);
                if (mutation == "directory") archive.CreateEntry("payload/");
                if (mutation == "symlink") payloadEntry!.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            }
            byte[] bytes = memory.ToArray();
            if (mutation == "metadata-limit") { bytes[^12] = 0xFF; bytes[^11] = 0xFF; }
            return Temp.Write(Guid.NewGuid().ToString("N") + ".cbpack", bytes);
        }
        private static ZipArchiveEntry Write(ZipArchive archive, string name, byte[] bytes)
        { var entry = archive.CreateEntry(name); using var output = entry.Open(); output.Write(bytes); return entry; }
        public void AssertNoTemporaryFiles()
        {
            string root = Temp.File("user/packs");
            if (!Directory.Exists(root)) return;
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories),
                p => Path.GetFileName(p).StartsWith(".staging-", StringComparison.Ordinal) || Path.GetFileName(p).StartsWith(".active-", StringComparison.Ordinal));
        }
        public void Dispose() { _key.Dispose(); Temp.Dispose(); }
    }
    private sealed class FaultFileSystem : IPackInstallFileSystem
    {
        private readonly PhysicalPackInstallFileSystem _physical = new();
        public bool FailActivation;
        public CancellationTokenSource? CancelPayload;
        public Action? UninstallObserved;
        public bool FileExists(string p) => _physical.FileExists(p);
        public bool DirectoryExists(string p) => _physical.DirectoryExists(p);
        public IReadOnlyList<string> Directories(string p) => _physical.Directories(p);
        public void CheckSafePath(string p) => _physical.CheckSafePath(p);
        public Stream OpenRead(string p) => _physical.OpenRead(p);
        public Stream CreateNew(string p)
        { if (p.EndsWith("fixture.dll", StringComparison.Ordinal)) CancelPayload?.Cancel(); return _physical.CreateNew(p); }
        public void CreateDirectory(string p) => _physical.CreateDirectory(p);
        public void MoveDirectory(string a, string b) => _physical.MoveDirectory(a, b);
        public void ActivateFile(string a, string b)
        { if (FailActivation) throw new IOException("Synthetic atomic replace failure."); _physical.ActivateFile(a, b); }
        public void DeleteFile(string p) => _physical.DeleteFile(p);
        public void DeleteDirectory(string p)
        { if (Path.GetFileName(p) == "powertrove.fixture") UninstallObserved?.Invoke(); _physical.DeleteDirectory(p); }
    }
}
