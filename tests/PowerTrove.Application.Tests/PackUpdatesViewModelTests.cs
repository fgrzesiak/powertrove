using PowerTrove.Application.Packs;
using PowerTrove.Application.ViewModels;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Application.Tests;

public sealed class PackUpdatesViewModelTests
{
    private static PackListItem Update(string version = "2.0.0") => new("powertrove.test", "Generated pack", "Tests", "1.0.0", version,
        10, 1, PackInstallState.UpdateAvailable, true, true, false);

    [Fact]
    public async Task OnlyInstalledCompatibleUpdatesAppear_AndSameVersionDismissalSurvivesChecks()
    {
        var clock = new Clock(); var service = new Service();
        service.Page = new(PackChannelStatus.Ready, [Update(), Update() with { Id = "missing", InstalledVersion = null },
            Update() with { Id = "built-in", CanUninstall = false }, Update() with { Id = "incompatible", CanDownload = false }]);
        using var model = new PackUpdatesViewModel(clock); model.Configure(service, true);
        Assert.Equal(PackChannelStatus.Ready, await model.CheckIfDueAsync());
        Assert.Equal(1, model.UpdateCount); Assert.True(model.ShowNotice);
        Assert.Contains("An update", model.NoticeMessage);
        model.DismissNotice(); Assert.False(model.ShowNotice);
        Assert.Equal(PackChannelStatus.Unavailable, await model.CheckIfDueAsync()); Assert.Equal(1, service.Checks);
        clock.Advance(PackUpdatesViewModel.CheckInterval);
        await model.CheckIfDueAsync(); Assert.False(model.ShowNotice); Assert.Equal(2, service.Checks);
        service.Page = new(PackChannelStatus.Ready, [Update("3.0.0")]);
        clock.Advance(PackUpdatesViewModel.CheckInterval);
        await model.CheckIfDueAsync(); Assert.True(model.ShowNotice);
    }

    [Theory]
    [InlineData(PackChannelStatus.Offline)]
    [InlineData(PackChannelStatus.TimedOut)]
    [InlineData(PackChannelStatus.InvalidData)]
    public async Task FailedChecksKeepVerifiedNotice_AndRetryIsThrottled(PackChannelStatus failure)
    {
        var clock = new Clock(); var service = new Service();
        using var model = new PackUpdatesViewModel(clock); model.Configure(service, true);
        await model.CheckIfDueAsync();
        service.Page = new(failure, []);
        clock.Advance(PackUpdatesViewModel.CheckInterval);
        Assert.Equal(failure, await model.CheckIfDueAsync()); Assert.True(model.ShowNotice);
        clock.Advance(TimeSpan.FromMinutes(14));
        await model.CheckIfDueAsync(); Assert.Equal(2, service.Checks);
        clock.Advance(TimeSpan.FromMinutes(1));
        await model.CheckIfDueAsync(); Assert.Equal(3, service.Checks);
    }

    [Fact]
    public async Task BusyCheckRetriesNextPoll_AndManualInstallationClearsNoticeThroughNotifications()
    {
        var service = new Service { Page = new(PackChannelStatus.Busy, []) };
        using var model = new PackUpdatesViewModel(); model.Configure(service, true);
        Assert.Equal(PackChannelStatus.Busy, await model.CheckIfDueAsync());
        service.Page = new(PackChannelStatus.Ready, [Update()]);
        await model.CheckIfDueAsync(); Assert.True(model.ShowNotice); Assert.Equal(2, service.Checks);
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, _) => { if (!model.ShowNotice) cleared.TrySetResult(); };
        service.Page = new(PackChannelStatus.Ready, [Update() with { InstalledVersion = "2.0.0", State = PackInstallState.Installed }]);
        service.NotifyChanged();
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, model.UpdateCount); Assert.Equal(2, service.Checks);
    }

    [Fact]
    public async Task OptOutSuppressesChecks_CancelsInFlight_AndReenableCanCheckImmediately()
    {
        var service = new Service(); using var model = new PackUpdatesViewModel(); model.Configure(service, false);
        await model.CheckIfDueAsync(); Assert.Equal(0, service.Checks);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Check = async token => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return service.Page; };
        model.SetEnabled(true);
        var pending = model.CheckIfDueAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PackChannelStatus.Busy, await model.CheckIfDueAsync());
        model.SetEnabled(false); model.SetEnabled(true);
        await pending; Assert.Equal(1, service.Checks);
        service.Check = null;
        await model.CheckIfDueAsync(); Assert.Equal(2, service.Checks); Assert.True(model.ShowNotice);
    }

    [Fact]
    public async Task CallerCancellationAndApplicationShutdownStopBackgroundWork()
    {
        var service = new Service();
        using var model = new PackUpdatesViewModel(); model.Configure(service, true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Check = async token => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return service.Page; };
        using var cancellation = new CancellationTokenSource();
        var pending = model.CheckIfDueAsync(cancellation.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        using var second = new PackUpdatesViewModel(); second.Configure(service, true);
        using var shutdown = new CancellationTokenSource();
        var running = second.RunAsync(shutdown.Token);
        shutdown.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5));
        second.Dispose();
        service.NotifyChanged(); // No late callback may access a disposed lifetime token.
    }

    [Fact]
    public async Task CachedCountsHaveGermanSingularAndPluralWithoutStartingDownloads()
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("de");
            var service = new Service(); using var model = new PackUpdatesViewModel(); model.Configure(service, false);
            await model.RefreshLocalAsync(); Assert.Contains("Für ein installiertes", model.NoticeMessage);
            service.Page = new(PackChannelStatus.Ready, [Update(), Update() with { Id = "second" }]);
            await model.RefreshLocalAsync(); Assert.Contains("Für 2 installierte", model.NoticeMessage); Assert.Equal(0, service.Checks);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
    private sealed class Service : IPackManagementService, IPackUpdateChecks, IPackManagementNotifications
    {
        public PackPageSnapshot Page = new(PackChannelStatus.Ready, [Update()]);
        public Func<CancellationToken, Task<PackPageSnapshot>>? Check;
        public int Checks;
        public event EventHandler? Changed;
        public event EventHandler? ActivityChanged { add { } remove { } }
        public PackOperation? Activity => null;
        public void Cancel() { }
        public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
        public Task<PackPageSnapshot> GetLocalAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Page); }
        public Task<PackPageSnapshot> CheckForUpdatesAsync(CancellationToken token) { Interlocked.Increment(ref Checks); return Check?.Invoke(token) ?? Task.FromResult(Page); }
        public Task<PackPageSnapshot> OpenOnUserRequestAsync(CancellationToken token) => throw new InvalidOperationException("Automatic checks must not open Settings or reload the runtime.");
        public Task<PackOfferResult> PrepareAsync(string id, CancellationToken token) => throw new InvalidOperationException("No installation consent.");
        public Task<PackChannelStatus> InstallAsync(PackInstallOffer offer, IProgress<PackDownloadProgress>? progress, CancellationToken token) => throw new InvalidOperationException("No automatic payload download.");
        public Task<PackChannelStatus> InstallFileAsync(string path, CancellationToken token) => throw new InvalidOperationException("No automatic installation.");
        public Task<PackChannelStatus> UninstallAsync(string id, CancellationToken token) => throw new InvalidOperationException("No automatic removal.");
    }
}
