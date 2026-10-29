using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Application.Packs;
using PowerTrove.Application.Updates;
using PowerTrove.Core;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Application.Tests;

public sealed class PackManagementIntegrationTests
{
    [Fact]
    public async Task SignedPackUpdateLocalizesDiscoveryAndInstalledParameters_WithoutChangingHostOrFingerprint()
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("de-AT");
            await using var f = new Fixture(localized: true);
            string old = f.Temp.File("old.cbpack"); File.WriteAllBytes(old, f.Bundles["1.0.0"]);
            await f.Service.InstallFileAsync(old, default);
            Assert.Equal("Bildwerkzeug 1.0.0", f.Registry.GetAction("powertrove.image.first")!.Descriptor.Title);
            var page = await f.Service.OpenOnUserRequestAsync(default);
            Assert.Equal("Bilder", Assert.Single(page.Packs).Name);
            Assert.Equal("Bildwerkzeug 1.1.0", f.Registry.GetAction("powertrove.image.new")!.Descriptor.Title);
            var germanOffer = (await f.Service.PrepareAsync("powertrove.images", default)).Offer!;
            Assert.Equal("Bilder", germanOffer.Name);
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en");
            var englishOffer = (await f.Service.PrepareAsync("powertrove.images", default)).Offer!;
            Assert.Equal(englishOffer.ManifestFingerprint, germanOffer.ManifestFingerprint);
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("de");
            Assert.Equal(PackChannelStatus.Installed, await f.Service.InstallAsync(germanOffer, null, default));
            var action = f.Registry.GetAction("powertrove.image.first")!;
            Assert.Equal("Bildwerkzeug 1.1.0", action.Descriptor.Title);
            var parameter = Assert.Single(action.Descriptor.Parameters!);
            Assert.Equal("Zielformat", parameter.Title); Assert.Equal("format", parameter.Name); Assert.Equal("png", parameter.DefaultValue);
            Assert.Equal("PNG-Bild", Assert.Single(parameter.Choices!).Title);
            Assert.Equal(new Version(1, 0, 0), f.Installer.HostVersion); Assert.Equal(0, f.WorkerStarts);
            using var offline = f.NewOfflineClient();
            Assert.Equal(PackChannelStatus.Ready, await offline.LoadCachedAsync());
            Assert.Equal("New remote title", offline.GetLatestCompatible("powertrove.images")!.Manifest.Actions[0].Title);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task ActualSignedChannelInstallsCompatibleVersion_AndRefreshesDiscoveryAfterInstallAndUninstall()
    {
        await using var f = new Fixture();
        await f.Service.InitializeAsync(); Assert.Empty(f.Registry.GetAllActions());
        var opened = await f.Service.OpenOnUserRequestAsync(default);
        var row = Assert.Single(opened.Packs);
        Assert.Equal("1.1.0", row.AvailableVersion); Assert.Equal("2.0.0", row.LatestVersion); Assert.Equal("2.0.0", row.RequiredHostVersion);
        Assert.True(row.CanDownload); Assert.Equal(2, f.Requests);
        var offer = (await f.Service.PrepareAsync("powertrove.images", default)).Offer!;
        Assert.Equal("1.1.0", offer.Version);
        Assert.Equal(PackChannelStatus.Installed, await f.Service.InstallAsync(offer, null, default));
        Assert.Equal("1.1.0", Assert.Single(await f.Installer.GetInstalledAsync()).Manifest.Version);
        Assert.All(f.Registry.GetAllActions(), action => Assert.IsType<RemoteAction>(action));
        Assert.Equal(3, f.Requests); Assert.Equal(0, f.WorkerStarts);
        await f.Service.UninstallAsync("powertrove.images", default);
        Assert.All(f.Registry.GetAllActions(), action => Assert.IsType<CatalogAction>(action));
        Assert.Empty(await f.Installer.GetInstalledAsync()); Assert.Equal(3, f.Requests);
    }

    [Fact]
    public async Task CatalogRefreshDoesNotReplaceInstalledTitlesOrPretendNewFeaturesAreInstalled()
    {
        await using var f = new Fixture();
        string path = f.Temp.File("local.cbpack"); File.WriteAllBytes(path, f.Bundles["1.0.0"]);
        await f.Service.InstallFileAsync(path, default);
        var installed = Assert.IsType<RemoteAction>(f.Registry.GetAction("powertrove.image.first"));
        Assert.Equal("Installed image tool", installed.Descriptor.Title);
        await f.Service.OpenOnUserRequestAsync(default);
        Assert.Equal("Installed image tool", f.Registry.GetAction("powertrove.image.first")!.Descriptor.Title);
        var newFeature = Assert.IsType<CatalogAction>(f.Registry.GetAction("powertrove.image.new"));
        Assert.Equal(PackInstallState.UpdateAvailable, newFeature.InstallState);
        var row = Assert.Single((await f.Service.GetLocalAsync(default)).Packs);
        Assert.Equal("1.0.0", row.InstalledVersion); Assert.Equal(1, row.ActionCount);
        Assert.Equal("1.1.0", row.AvailableVersion); Assert.Equal(0, f.WorkerStarts);
    }

    [Fact]
    public async Task VerifiedCatalogIsReusedOffline_AndTamperedCacheIsNeverTrusted()
    {
        await using var f = new Fixture();
        await f.Channel.RefreshOnUserRequestAsync();
        int before = f.Requests;
        using (var cached = f.NewOfflineClient())
        {
            Assert.Equal(PackChannelStatus.Ready, await cached.LoadCachedAsync());
            Assert.Equal(3, cached.CachedPacks.Count); Assert.Equal(before, f.Requests);
        }
        string path = f.Temp.File("user/pack-catalog.cache"); byte[] bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        using var tampered = f.NewOfflineClient();
        Assert.Equal(PackChannelStatus.InvalidData, await tampered.LoadCachedAsync());
        Assert.Empty(tampered.CachedPacks); Assert.Equal(before, f.Requests);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public TempDirectory Temp { get; } = new();
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly PackSignatureVerifier _signatures;
        private readonly PackHostSupervisor _supervisor;
        public Dictionary<string, byte[]> Bundles { get; } = [];
        public ActionRegistry Registry { get; } = new();
        public PackInstaller Installer { get; }
        public PackChannelClient Channel { get; }
        public PackManagementService Service { get; }
        public int Requests, WorkerStarts;
        public Fixture(bool localized = false)
        {
            _signatures = new([Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo())]);
            var manifests = new[] { Manifest("1.0.0", "1.0.0", false), Manifest("1.1.0", "1.0.0", true), Manifest("2.0.0", "2.0.0", true) }
                .Select(m => !localized ? m : ManifestLocalization.WithTranslation(m, "de", m with
                {
                    DisplayName = "Bilder", Actions = m.Actions.Select(a => a with { Title = "Bildwerkzeug " + m.Version,
                        Parameters = a.Parameters?.Select(p => p with { Title = "Zielformat",
                            ChoiceTitles = new Dictionary<string, string> { ["png"] = "PNG-Bild" } }).ToArray() }).ToArray()
                })).ToArray();
            foreach (var manifest in manifests) Bundles.Add(manifest.Version, Bundle(manifest));
            byte[] catalog = JsonSerializer.SerializeToUtf8Bytes(new
            { schemaVersion = "2", packs = manifests.Select(m => new { manifest = m, downloadBytes = Bundles[m.Version].Length }).ToArray() });
            byte[] signature = _key.SignData(catalog, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            Installer = new(Temp.File("user"), new Version(1, 0, 0), (_, _) => Task.CompletedTask, signatures: _signatures);
            Channel = new(Configuration(), new Handler(request =>
            {
                Requests++;
                string path = request.RequestUri!.AbsolutePath;
                return path.EndsWith("catalog.json.sig") ? signature : path.EndsWith("catalog.json") ? catalog :
                    Bundles[Path.GetFileNameWithoutExtension(path)];
            }), Installer, Temp.File("user"), new Version(1, 0, 0), _signatures);
            _supervisor = new(new NoWorker(() => WorkerStarts++), Temp.File("user"));
            var runtime = new InstalledPackRuntime(Installer, _supervisor, Registry, [], ActionCatalog.Empty);
            Service = new(Installer, Channel, runtime, [], ActionCatalog.Empty);
        }
        public PackChannelClient NewOfflineClient() => new(Configuration(), new Handler(_ => throw new InvalidOperationException("No HTTP allowed.")),
            Installer, Temp.File("user"), new Version(1, 0, 0), _signatures);
        private static OwnerChannelConfiguration Configuration() => new(new Uri("https://owner.example/channel/"));
        private static ActionManifest Manifest(string version, string minimum, bool added)
        {
            byte[] payload = "generated registration fixture, not executable"u8.ToArray();
            ManifestAction Action(string id, string title) => new(id, title, ["files"], Extensions: [".png"],
                Parameters: [new("format", "choice", "Target format", Choices: ["png"], Default: "png", ChoiceTitles: new Dictionary<string, string> { ["png"] = "PNG image" })],
                Selection: ManifestSelection.FromRequirements(SelectionRequirements.Any("image")), ChangesFiles: true);
            return new("2.0", "powertrove.images", version, "Images", "PowerTrove tests", "1",
                added ? [Action("powertrove.image.first", "New remote title"), Action("powertrove.image.new", "New image tool")]
                    : [Action("powertrove.image.first", "Installed image tool")],
                PlatformConstraints: new(["windows"], ["x64"]), MinHostVersion: minimum, Capabilities: [],
                Payload: new("payload/fixture.dll", payload.Length, [new("payload/fixture.dll", Convert.ToHexString(SHA256.HashData(payload)), payload.Length)]));
        }
        private byte[] Bundle(ActionManifest manifest)
        {
            byte[] json = Encoding.UTF8.GetBytes(ActionPackManifest.ToJson(manifest));
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                Write(zip, "manifest.json", json); Write(zip, "manifest.sig", _key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
                Write(zip, "payload/fixture.dll", "generated registration fixture, not executable"u8.ToArray());
            }
            return memory.ToArray();
        }
        private static void Write(ZipArchive zip, string name, byte[] bytes) { using var output = zip.CreateEntry(name).Open(); output.Write(bytes); }
        public async ValueTask DisposeAsync() { Channel.Dispose(); await _supervisor.DisposeAsync(); _key.Dispose(); Temp.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, byte[]> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(respond(request)) });
    }
    private sealed class NoWorker(Action started) : IPackWorkerProcessFactory
    {
        public Task<IPackWorkerProcess> StartAsync(PackWorkerGroup group, string directory, CancellationToken token)
        { started(); throw new InvalidOperationException("Listing/registration never launches a worker."); }
    }
}
