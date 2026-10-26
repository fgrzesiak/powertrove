using System.Globalization;
using ContextBar.Application.Updates;
using ContextBar.Application.ViewModels;

namespace ContextBar.Application.Tests;

public sealed class ReleaseUpdatesViewModelTests
{
    [Fact]
    public async Task ConstructionDoesNotCheck_AndCheckingNeverOpensBrowser_UntilSeparateClick()
    {
        var discovery = new Discovery(); var launcher = new Launcher();
        using var vm = new ReleaseUpdatesViewModel("1.0.0", discovery, launcher);
        Assert.Equal(0, discovery.Count); Assert.Empty(launcher.Pages); Assert.False(vm.CanOpen);
        await vm.OpenReleasePageAsync(); Assert.Empty(launcher.Pages);
        await vm.CheckAsync(); Assert.Equal(1, discovery.Count); Assert.True(vm.CanOpen); Assert.Empty(launcher.Pages);
        await vm.OpenReleasePageAsync(); Assert.Equal(ReleaseSource.ForTag("v2.0.0"), Assert.Single(launcher.Pages));
    }

    [Fact]
    public async Task ConcurrentCheckAndOpen_AreDisabledWhileChecking()
    {
        var pending = new TaskCompletionSource<ReleaseCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new Discovery { Pending = pending.Task }; var launcher = new Launcher();
        using var vm = new ReleaseUpdatesViewModel("1.0.0", discovery, launcher);
        var check = vm.CheckAsync(); Assert.True(vm.IsBusy); Assert.False(vm.CanCheck); Assert.False(vm.CanOpen);
        await vm.CheckAsync(); await vm.OpenReleasePageAsync(); Assert.Equal(1, discovery.Count); Assert.Empty(launcher.Pages);
        pending.SetResult(discovery.Result); await check; Assert.True(vm.CanCheck); Assert.True(vm.CanOpen);
    }

    [Fact]
    public async Task ClosingSettings_CancelsPendingCheck_AndIgnoresLateCompletion()
    {
        var pending = new TaskCompletionSource<ReleaseCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new Discovery { Pending = pending.Task }; var launcher = new Launcher();
        var vm = new ReleaseUpdatesViewModel("1.0.0", discovery, launcher);
        var check = vm.CheckAsync(); vm.Dispose(); Assert.True(discovery.Token.IsCancellationRequested);
        pending.SetResult(discovery.Result); await check;
        Assert.False(vm.CanCheck); Assert.False(vm.CanOpen); await vm.OpenReleasePageAsync(); Assert.Empty(launcher.Pages);
    }

    [Theory]
    [InlineData("https://evil.test/setup.exe")]
    [InlineData("https://github.com/fgrzesiak/powertrove/releases/download/v2.0.0/setup.exe")]
    [InlineData("https://github.com/fgrzesiak/powertrove/releases/tag/v3.0.0")]
    public async Task UnsafeOrMismatchedCandidates_NeverEnableOpening(string page)
    {
        var discovery = new Discovery { Result = new(ReleaseCheckStatus.Available, new(GitHubReleaseDiscoveryTests.Version("2.0.0"), new Uri(page))) };
        var launcher = new Launcher(); using var vm = new ReleaseUpdatesViewModel("1.0.0", discovery, launcher);
        await vm.CheckAsync(); Assert.False(vm.CanOpen); await vm.OpenReleasePageAsync(); Assert.Empty(launcher.Pages);
    }

    [Fact]
    public async Task StableInstallsRejectPrereleaseCandidates_AndUnknownInstalledVersionsDoNotCheck()
    {
        var discovery = new Discovery { Result = new(ReleaseCheckStatus.Available,
            new(GitHubReleaseDiscoveryTests.Version("2.0.0-beta.1"), ReleaseSource.ForTag("v2.0.0-beta.1"))) };
        using var stable = new ReleaseUpdatesViewModel("1.0.0", discovery, new Launcher());
        await stable.CheckAsync(); Assert.False(stable.CanOpen);
        using var unknown = new ReleaseUpdatesViewModel("unknown", discovery, new Launcher());
        Assert.False(unknown.CanCheck); await unknown.CheckAsync(); Assert.Equal(1, discovery.Count);
    }

    [Fact]
    public async Task NewCheck_RetiresPreviousCandidate_AndFailuresNeverShowRawDetails()
    {
        var discovery = new Discovery(); var launcher = new Launcher(); using var vm = new ReleaseUpdatesViewModel("1.0.0", discovery, launcher);
        await vm.CheckAsync(); Assert.True(vm.CanOpen);
        discovery.Failure = new InvalidOperationException("private-path-marker");
        await vm.CheckAsync(); Assert.False(vm.CanOpen); Assert.DoesNotContain("private", vm.StatusMessage);
        discovery.Failure = null; await vm.CheckAsync(); launcher.Failure = new InvalidOperationException("private-launch-marker");
        await vm.OpenReleasePageAsync(); Assert.DoesNotContain("private", vm.StatusMessage);
    }

    [Theory]
    [InlineData(ReleaseCheckStatus.UpToDate)]
    [InlineData(ReleaseCheckStatus.NoReleases)]
    [InlineData(ReleaseCheckStatus.Offline)]
    [InlineData(ReleaseCheckStatus.TimedOut)]
    [InlineData(ReleaseCheckStatus.RateLimited)]
    [InlineData(ReleaseCheckStatus.InvalidData)]
    [InlineData(ReleaseCheckStatus.Unavailable)]
    public async Task FailureAndStatusMessages_AreLocalized(ReleaseCheckStatus status)
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            var discovery = new Discovery { Result = new(status) };
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            using var english = new ReleaseUpdatesViewModel("1.0.0", discovery, new Launcher()); await english.CheckAsync();
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            using var german = new ReleaseUpdatesViewModel("1.0.0", discovery, new Launcher()); await german.CheckAsync();
            Assert.NotEqual(english.StatusMessage, german.StatusMessage);
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    private sealed class Discovery : IReleaseDiscovery
    {
        public int Count { get; private set; }
        public CancellationToken Token { get; private set; }
        public ReleaseCheckResult Result { get; set; } = new(ReleaseCheckStatus.Available,
            new(GitHubReleaseDiscoveryTests.Version("2.0.0"), ReleaseSource.ForTag("v2.0.0")));
        public Task<ReleaseCheckResult>? Pending { get; set; }
        public Exception? Failure { get; set; }
        public Task<ReleaseCheckResult> CheckAsync(ReleaseVersion version, CancellationToken ct = default)
        { Count++; Token = ct; return Failure is null ? Pending ?? Task.FromResult(Result) : Task.FromException<ReleaseCheckResult>(Failure); }
    }
    private sealed class Launcher : IReleasePageLauncher
    {
        public List<Uri> Pages { get; } = new();
        public Exception? Failure { get; set; }
        public Task<bool> OpenAsync(Uri page, CancellationToken ct = default)
        { Pages.Add(page); return Failure is null ? Task.FromResult(true) : Task.FromException<bool>(Failure); }
    }
}
