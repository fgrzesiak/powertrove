using System.Net;
using System.Security.Cryptography;
using PowerTrove.Application.Updates;

namespace PowerTrove.Application.Tests;

public sealed class GitHubReleaseInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PowerTrove-update-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = [0x4d, 0x5a, 1, 2, 3, 4];
    private static ReleaseCandidate Candidate() => new(GitHubReleaseDiscoveryTests.Version("2.0.0-alpha.2"),
        ReleaseSource.ForTag("v2.0.0-alpha.2"), new(
            new Uri("https://github.com/" + ReleaseSource.Repository + "/releases/download/v2.0.0-alpha.2/PowerTrove-v2.0.0-win-x64-installer.exe"),
            Payload.Length, Convert.ToHexString(SHA256.HashData(Payload))));

    [Fact]
    public async Task ExplicitInstall_VerifiesExactBytesBeforeLaunching_AndReportsProgress()
    {
        var launcher = new Launcher(); var handler = new Handler(_ => Response(Payload));
        using var service = Service(handler, launcher);
        Assert.Empty(handler.Uris);
        List<int> progress = [];
        Assert.Equal(ReleaseInstallStatus.Started, await service.InstallAsync(Candidate(), new ProgressSink(progress.Add)));
        Assert.Equal(Payload, launcher.Bytes); Assert.Equal(1, launcher.Count);
        Assert.Equal(Candidate().Installer!.Download, Assert.Single(handler.Uris));
        Assert.Contains(100, progress);
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("header")]
    public async Task CorruptOrWrongSizedDownload_NeverLaunches_AndRemovesStaging(string mutation)
    {
        var candidate = Candidate();
        byte[] data = mutation switch { "short" => Payload[..^1], "long" => [.. Payload, 9], _ => [.. Payload] };
        if (mutation == "hash") data[0] ^= 1;
        var launcher = new Launcher();
        using var service = Service(new Handler(_ =>
        {
            var response = Response(data);
            response.Content.Headers.ContentLength = mutation == "header" ? 99 : null;
            return response;
        }), launcher);
        Assert.Equal(ReleaseInstallStatus.InvalidData, await service.InstallAsync(candidate));
        Assert.Equal(0, launcher.Count); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Theory]
    [InlineData("https://evil.test/setup.exe")]
    [InlineData("http://github.com/fgrzesiak/powertrove/releases/download/v2.0.0-alpha.2/PowerTrove-v2.0.0-win-x64-installer.exe")]
    [InlineData("https://github.com/other/repo/releases/download/v2.0.0-alpha.2/PowerTrove-v2.0.0-win-x64-installer.exe")]
    public async Task UntrustedMetadata_IsRejectedBeforeAnyRequest(string url)
    {
        var handler = new Handler(_ => Response(Payload)); var launcher = new Launcher();
        using var service = Service(handler, launcher);
        var candidate = Candidate();
        candidate = candidate with { Installer = candidate.Installer! with { Download = new Uri(url) } };
        Assert.Equal(ReleaseInstallStatus.InvalidData, await service.InstallAsync(candidate));
        Assert.Empty(handler.Uris); Assert.Equal(0, launcher.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(GitHubReleaseInstaller.MaximumInstallerBytes + 1)]
    public void InvalidAssetSizes_AreRejected(long size)
    {
        var candidate = Candidate();
        Assert.False(GitHubReleaseInstaller.IsValidAsset(candidate with { Installer = candidate.Installer! with { Size = size } }));
    }

    [Theory]
    [InlineData("https://evil.test/update", false)]
    [InlineData("http://release-assets.githubusercontent.com/update", false)]
    [InlineData("https://release-assets.githubusercontent.com:444/update", false)]
    [InlineData("https://user@release-assets.githubusercontent.com/update", false)]
    [InlineData("https://release-assets.githubusercontent.com/update?signature=value", true)]
    public async Task Redirects_OnlyReachAllowedHttpsDownloadHosts(string location, bool allowed)
    {
        var launcher = new Launcher(); int requests = 0;
        using var service = Service(new Handler(_ => ++requests == 1
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } }
            : Response(Payload)), launcher);
        Assert.Equal(allowed ? ReleaseInstallStatus.Started : ReleaseInstallStatus.InvalidData, await service.InstallAsync(Candidate()));
        Assert.Equal(allowed ? 2 : 1, requests); Assert.Equal(allowed ? 1 : 0, launcher.Count);
    }

    [Fact]
    public async Task RedirectLoops_AreBounded()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/loop") } });
        using var service = Service(handler, new Launcher());
        Assert.Equal(ReleaseInstallStatus.InvalidData, await service.InstallAsync(Candidate())); Assert.Equal(6, handler.Uris.Count);
    }

    [Fact]
    public async Task CancellationDuringDownload_CleansStaging_AndDoesNotLaunch()
    {
        using var cancel = new CancellationTokenSource(); var launcher = new Launcher();
        var handler = new AsyncHandler(async token => { cancel.Cancel(); await Task.Delay(Timeout.Infinite, token); return Response(Payload); });
        using var service = new GitHubReleaseInstaller(handler, launcher, _root);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InstallAsync(Candidate(), cancellationToken: cancel.Token));
        Assert.Equal(0, launcher.Count); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task DownloadTimeout_IsBoundedWithoutRetries_AndCleansStaging()
    {
        var clock = new ManualTimeout(); var launcher = new Launcher(); int requests = 0;
        using var service = new GitHubReleaseInstaller(new AsyncHandler(async token =>
        {
            requests++; await Task.Delay(Timeout.Infinite, token); return Response(Payload);
        }), launcher, _root, timeProvider: clock);
        var install = service.InstallAsync(Candidate());
        Assert.Equal(GitHubReleaseInstaller.DownloadTimeout, clock.DueTime);
        clock.Fire();
        Assert.Equal(ReleaseInstallStatus.TimedOut, await install);
        Assert.Equal(1, requests); Assert.Equal(0, launcher.Count); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task IdleState_IsCheckedAgainAfterDownload()
    {
        bool idle = true; var launcher = new Launcher();
        using var service = Service(new Handler(_ => { idle = false; return Response(Payload); }), launcher, () => idle);
        Assert.Equal(ReleaseInstallStatus.Busy, await service.InstallAsync(Candidate()));
        Assert.Equal(0, launcher.Count); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task BusyApp_DoesNotDownload_AndConcurrentInstallsAreRejected()
    {
        var handler = new Handler(_ => Response(Payload)); var launcher = new Launcher();
        using (var busy = Service(handler, launcher, () => false))
            Assert.Equal(ReleaseInstallStatus.Busy, await busy.InstallAsync(Candidate()));
        Assert.Empty(handler.Uris);
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        using var service = new GitHubReleaseInstaller(new AsyncHandler(_ => pending.Task), launcher, _root);
        var first = service.InstallAsync(Candidate());
        Assert.Equal(ReleaseInstallStatus.Busy, await service.InstallAsync(Candidate()));
        pending.SetResult(Response(Payload)); Assert.Equal(ReleaseInstallStatus.Started, await first);
    }

    [Fact]
    public async Task OfflineAndLaunchFailure_AreSafeAndCleanUp()
    {
        var launcher = new Launcher();
        using (var offline = Service(new Handler(_ => throw new HttpRequestException("private-marker")), launcher))
            Assert.Equal(ReleaseInstallStatus.Offline, await offline.InstallAsync(Candidate()));
        Assert.Empty(Directory.GetDirectories(_root)); Assert.Equal(0, launcher.Count);
        launcher.Success = false;
        using var failure = Service(new Handler(_ => Response(Payload)), launcher);
        Assert.Equal(ReleaseInstallStatus.Failed, await failure.InstallAsync(Candidate()));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public void Cleanup_OnlyRemovesOldOwnedDownloads()
    {
        string old = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        string fresh = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        string unrelated = Path.Combine(_root, "unrelated");
        foreach (string dir in new[] { old, fresh, unrelated }) Directory.CreateDirectory(dir);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-2));
        using var service = Service(new Handler(_ => Response(Payload)), new Launcher()); service.CleanupStaleDownloads();
        Assert.False(Directory.Exists(old)); Assert.True(Directory.Exists(fresh)); Assert.True(Directory.Exists(unrelated));
    }

    private GitHubReleaseInstaller Service(HttpMessageHandler handler, Launcher launcher, Func<bool>? idle = null) => new(handler, launcher, _root, idle);
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class ProgressSink(Action<int> report) : IProgress<int> { public void Report(int value) => report(value); }
    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            Uris.Add(request.RequestUri!); return Task.FromResult(respond(request.RequestUri!));
        }
    }
    private sealed class AsyncHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(token);
    }
    private sealed class Launcher : IReleaseInstallerLauncher
    {
        public int Count; public byte[]? Bytes; public bool Success = true;
        public Task<bool> StartAsync(string path, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Count++; Bytes = File.ReadAllBytes(path); return Task.FromResult(Success); }
    }
    private sealed class ManualTimeout : TimeProvider
    {
        private TimerCallback? _callback; private object? _state;
        public TimeSpan DueTime;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _callback = callback; _state = state; DueTime = dueTime; return new Timer(); }
        public void Fire() => _callback!(_state);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
