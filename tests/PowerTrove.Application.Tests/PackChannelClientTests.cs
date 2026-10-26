using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Application.Packs;
using PowerTrove.Application.Updates;

namespace PowerTrove.Application.Tests;

public sealed class PackChannelClientTests
{
    [Fact]
    public async Task ConstructionCachedAccessAndLocalDiscoveryNeverSendRequests()
    {
        using var fixture = new Fixture();
        Assert.Empty(fixture.Client.CachedPacks);
        Assert.Empty(await fixture.Installer.GetInstalledAsync());
        Assert.Empty(fixture.Handler.Requests);
        var result = await fixture.Client.RefreshOnUserRequestAsync();
        Assert.Equal(PackChannelStatus.Ready, result.Status);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Single(result.Packs);
        Assert.All(fixture.Handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.Uri.Scheme);
            Assert.Empty(request.Uri.Query);
            Assert.DoesNotContain(request.Headers, h => h.Key is "Authorization" or "Cookie");
        });
    }

    [Fact]
    public async Task MissingProductionChannelDoesNotSendEvenAnExplicitRequest()
    {
        using var fixture = new Fixture(configuration: OwnerChannelConfiguration.Default);
        Assert.Equal(PackChannelStatus.NotConfigured, (await fixture.Client.RefreshOnUserRequestAsync()).Status);
        Assert.Equal(PackChannelStatus.NotConfigured, (await fixture.Client.InstallOnUserRequestAsync("contextbar.fixture")).Status);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task ValidDownloadInstallsOnlyTheCatalogManifestReportsProgressAndDeletesTemporaryFile()
    {
        using var fixture = new Fixture();
        var progress = new CapturedProgress();
        var result = await fixture.Client.InstallOnUserRequestAsync("contextbar.fixture", progress);
        Assert.Equal(PackChannelStatus.Installed, result.Status);
        Assert.Equal("contextbar.fixture", result.Installed!.Manifest.Id);
        Assert.Equal(3, fixture.Handler.Requests.Count);
        Assert.Equal("/channel/packs/contextbar.fixture/0.1.0.cbpack", fixture.Handler.Requests[^1].Uri.AbsolutePath);
        Assert.Equal(fixture.Bundle.Length, progress.Values[^1].CompletedBytes);
        Assert.Single(await fixture.Installer.GetInstalledAsync());
        fixture.AssertCleanDownloads();
    }

    [Fact]
    public async Task ChangedApprovalFingerprintCannotStartABundleRequest()
    {
        using var fixture = new Fixture();
        var prepared = await fixture.Client.PrepareOnUserRequestAsync("contextbar.fixture");
        Assert.Equal(PackChannelStatus.Ready, prepared.Status);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        var changed = prepared.Offer! with { ManifestFingerprint = new string('0', 64) };
        var result = await fixture.Client.InstallOnUserRequestAsync(changed.Id, approvedOffer: changed);
        Assert.Equal(PackChannelStatus.InvalidData, result.Status);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Empty(await fixture.Installer.GetInstalledAsync());
    }

    [Fact]
    public async Task InvalidCatalogSignatureStopsBeforeBundleAccess()
    {
        using var fixture = new Fixture();
        fixture.Signature[0] ^= 1;
        Assert.Equal(PackChannelStatus.InvalidData, (await fixture.Client.InstallOnUserRequestAsync("contextbar.fixture")).Status);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Empty(await fixture.Installer.GetInstalledAsync());
    }

    [Fact]
    public async Task StreamingOverflowWithoutContentLengthStopsAtTheBoundAndCleansTheDownload()
    {
        using var fixture = new Fixture(downloadBytes: 0);
        var overflow = new RepeatedStream(fixture.PayloadSize + 1024 * 1024 + 2L);
        fixture.BundleContent = () => new StreamContent(overflow);
        var result = await fixture.Client.InstallOnUserRequestAsync("contextbar.fixture");
        Assert.Equal(PackChannelStatus.InvalidData, result.Status);
        Assert.Equal(fixture.PayloadSize + 1024 * 1024 + 1L, overflow.ReadBytes);
        Assert.Empty(await fixture.Installer.GetInstalledAsync());
        fixture.AssertCleanDownloads();
    }

    [Fact]
    public async Task CatalogBodyOverflowAndRedirectsAreRejectedWithoutFollowingAnotherUrl()
    {
        using var fixture = new Fixture();
        var overflow = new RepeatedStream(4 * 1024 * 1024 + 1L);
        fixture.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(overflow) });
        Assert.Equal(PackChannelStatus.InvalidData, (await fixture.Client.RefreshOnUserRequestAsync()).Status);
        Assert.Equal(4 * 1024 * 1024 + 1L, overflow.ReadBytes);
        Assert.Single(fixture.Handler.Requests);
        fixture.Handler.Respond = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://other.example/"); return Task.FromResult(response);
        };
        Assert.Equal(PackChannelStatus.InvalidData, (await fixture.Client.RefreshOnUserRequestAsync()).Status);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.All(fixture.Handler.Requests, request => Assert.Equal("owner.example", request.Uri.Host));
    }

    [Fact]
    public async Task StalledDownloadTimesOutAndRemovesAnAlreadyCreatedTemporaryFile()
    {
        var clock = new PackHostSupervisorTests.TestClock();
        using var fixture = new Fixture(clock: clock);
        var blocked = new RepeatedStream(1, block: true);
        fixture.BundleContent = () => new StreamContent(blocked);
        var work = fixture.Client.InstallOnUserRequestAsync("contextbar.fixture");
        await blocked.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.TemporaryCount);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(PackChannelStatus.TimedOut, (await work.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        fixture.AssertCleanDownloads();
        Assert.Empty(await fixture.Installer.GetInstalledAsync());
    }

    [Fact]
    public async Task OfflineHasNoRetriesAndKeepsInstalledDiscoveryUsable()
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = (_, _) => throw new HttpRequestException("Synthetic offline fixture.");
        Assert.Equal(PackChannelStatus.Offline, (await fixture.Client.RefreshOnUserRequestAsync()).Status);
        Assert.Single(fixture.Handler.Requests);
        Assert.Empty(fixture.Client.CachedPacks);
        Assert.Empty(await fixture.Installer.GetInstalledAsync());
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task TenSecondCatalogTimeoutUsesTheInjectedClock()
    {
        var clock = new PackHostSupervisorTests.TestClock();
        using var fixture = new Fixture(clock: clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Respond = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); };
        var work = fixture.Client.RefreshOnUserRequestAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(PackChannelStatus.TimedOut, (await work.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task UserCancellationStopsHttpAndDoesNotActivateAPack()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Respond = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); };
        var work = fixture.Client.InstallOnUserRequestAsync("contextbar.fixture", token: cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Single(fixture.Handler.Requests);
        Assert.Empty(await fixture.Installer.GetInstalledAsync());
        fixture.AssertCleanDownloads();
    }

    [Fact]
    public async Task AValidSignatureCannotSubstituteADifferentVersionThanTheCatalog()
    {
        using var fixture = new Fixture(downloadBytes: 0);
        fixture.BundleContent = () => new ByteArrayContent(fixture.CreateBundle("0.2.0"));
        Assert.Equal(PackChannelStatus.InvalidData, (await fixture.Client.InstallOnUserRequestAsync("contextbar.fixture")).Status);
        Assert.Empty(await fixture.Installer.GetInstalledAsync());
        fixture.AssertCleanDownloads();
    }

    [Fact]
    public async Task FutureHostRequirementIsListedButCannotBeDownloaded()
    {
        using var fixture = new Fixture(minimum: "99.0.0");
        var catalog = await fixture.Client.RefreshOnUserRequestAsync();
        Assert.Equal(PackChannelStatus.Ready, catalog.Status);
        Assert.False(Assert.Single(catalog.Packs).IsCompatible);
        Assert.Equal(PackChannelStatus.Incompatible, (await fixture.Client.InstallOnUserRequestAsync("contextbar.fixture")).Status);
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("http://owner.example/channel/")]
    [InlineData("https://user:password@owner.example/channel/")]
    [InlineData("https://owner.example:444/channel/")]
    [InlineData("https://owner.example/channel/?device=example")]
    [InlineData("https://owner.example/channel/#fragment")]
    [InlineData("https://owner.example/channel")]
    public void InvalidOwnerChannelsAreRejected(string value) => Assert.Throws<ArgumentException>(() => new OwnerChannelConfiguration(new Uri(value)));

    private sealed class CapturedProgress : IProgress<PackDownloadProgress>
    { public List<PackDownloadProgress> Values { get; } = []; public void Report(PackDownloadProgress value) => Values.Add(value); }
    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ActionManifest _manifest;
        public RecordingHandler Handler { get; } = new();
        public PackInstaller Installer { get; }
        public PackChannelClient Client { get; }
        public byte[] Bundle { get; }
        public byte[] Catalog { get; }
        public byte[] Signature { get; }
        public Func<HttpContent> BundleContent { get; set; }
        public long PayloadSize => _manifest.Payload!.SizeBytes;
        public int TemporaryCount => Directory.Exists(_temp.File("user/downloads")) ? Directory.GetFiles(_temp.File("user/downloads")).Length : 0;
        public Fixture(OwnerChannelConfiguration? configuration = null, TimeProvider? clock = null, long? downloadBytes = null, string minimum = "0.1.0")
        {
            byte[] payload = "generated fixture payload"u8.ToArray();
            _manifest = new ActionManifest("2.0", "contextbar.fixture", "0.1.0", "Generated fixture tools", "PowerTrove tests", "1",
                [new ManifestAction("contextbar.fixture.copy", "Generated copy", ["files"], RequiresPreview: true,
                    Selection: new ManifestSelection(["text"], 1, null, null, null, false), Extensions: [".txt"], ChangesFiles: true)],
                MinHostVersion: minimum, Payload: new ManifestPayload("payload/fixture.dll", payload.LongLength,
                    [new ManifestPayloadFile("payload/fixture.dll", Convert.ToHexString(SHA256.HashData(payload)), payload.LongLength)]), Capabilities: []);
            Bundle = CreateBundle("0.1.0"); BundleContent = () => new ByteArrayContent(Bundle);
            using var manifestDocument = JsonDocument.Parse(ActionPackManifest.ToJson(_manifest));
            Catalog = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "1", packs = new[] { new { manifest = manifestDocument.RootElement, downloadBytes = downloadBytes ?? Bundle.LongLength } } });
            Signature = _key.SignData(Catalog, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var signatures = new PackSignatureVerifier([Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo())]);
            Installer = new PackInstaller(_temp.File("user"), new Version(0, 1, 0), (_, _) => Task.CompletedTask, signatures: signatures);
            Handler.Respond = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = request.RequestUri!.AbsolutePath.EndsWith("catalog.json.sig", StringComparison.Ordinal) ? new ByteArrayContent(Signature)
                : request.RequestUri.AbsolutePath.EndsWith("catalog.json", StringComparison.Ordinal) ? new ByteArrayContent(Catalog) : BundleContent() });
            Client = new PackChannelClient(configuration ?? new OwnerChannelConfiguration(new Uri("https://owner.example/channel/")),
                Handler, Installer, _temp.File("user"), new Version(0, 1, 0), signatures, clock: clock);
        }
        public byte[] CreateBundle(string version)
        {
            byte[] manifest = Encoding.UTF8.GetBytes(ActionPackManifest.ToJson(_manifest with { Version = version }));
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(archive, "manifest.json", manifest);
                Write(archive, "manifest.sig", _key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
                Write(archive, "payload/fixture.dll", "generated fixture payload"u8.ToArray());
            }
            return memory.ToArray();
        }
        private static void Write(ZipArchive archive, string name, byte[] bytes) { using var stream = archive.CreateEntry(name).Open(); stream.Write(bytes); }
        public void AssertCleanDownloads()
        { string root = _temp.File("user/downloads"); if (Directory.Exists(root)) Assert.Empty(Directory.EnumerateFileSystemEntries(root)); }
        public void Dispose() { Client.Dispose(); _key.Dispose(); _temp.Dispose(); }
    }
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(Uri Uri, HttpMethod Method, KeyValuePair<string, IEnumerable<string>>[] Headers)> Requests { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) => throw new InvalidOperationException();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add((request.RequestUri!, request.Method, request.Headers.ToArray())); return Respond(request, cancellationToken); }
    }
    private sealed class RepeatedStream(long remaining, bool block = false) : Stream
    {
        public long ReadBytes { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); ReadStarted.TrySetResult();
            if (block) return new(BlockedAsync(token));
            int count = (int)Math.Min(remaining, buffer.Length); buffer.Span[..count].Fill(1); remaining -= count; ReadBytes += count; return new(count);
        }
        private static async Task<int> BlockedAsync(CancellationToken token) { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
