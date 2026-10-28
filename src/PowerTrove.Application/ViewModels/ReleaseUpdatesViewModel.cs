using System.ComponentModel;
using System.Globalization;
using PowerTrove.Application.Resources;
using PowerTrove.Application.Updates;

namespace PowerTrove.Application.ViewModels;

public sealed class ReleaseUpdatesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IReleaseDiscovery _discovery;
    private readonly IReleasePageLauncher _launcher;
    private readonly IReleaseInstaller? _installer;
    private CancellationTokenSource? _installation;
    private bool _startupChecked, _noticeDismissed;
    public event EventHandler? InstallerStarted;
    private readonly ReleaseVersion? _installed;
    private readonly CancellationTokenSource _lifetime = new();
    private ReleaseCandidate? _candidate;
    private bool _busy, _closed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string InstalledVersionText { get; }
    public string StatusMessage { get; private set; }
    public bool IsBusy => _busy;
    public bool CanCheck => !_closed && !_busy && _installed is not null;
    public bool CanInstall => !_closed && !_busy && _candidate?.Installer is not null && _installer is not null && GitHubReleaseInstaller.IsValidAsset(_candidate);
    public bool IsInstalling => _installation is not null;
    public bool CanCancelInstall => IsInstalling && DownloadPercent < 100;
    public bool ShowNotice => !_closed && !_noticeDismissed && _candidate is not null;
    public string NoticeMessage => ShowNotice ? StatusMessage : string.Empty;
    public int DownloadPercent { get; private set; }
    public bool CanOpen => !_closed && !_busy && _candidate is not null;

    public ReleaseUpdatesViewModel(string? installedVersion, IReleaseDiscovery discovery, IReleasePageLauncher launcher, IReleaseInstaller? installer = null)
    {
        _discovery = discovery; _launcher = launcher; _installer = installer;
        ReleaseVersion.TryParse(installedVersion, out _installed);
        InstalledVersionText = _installed is null ? Text("UpdatesVersionUnknown") :
            string.Format(CultureInfo.CurrentCulture, Text("UpdatesInstalledVersion"), _installed.Display);
        StatusMessage = Text(_installed is null ? "UpdatesVersionUnknown" : "UpdatesNotChecked");
    }

    public async Task CheckOnStartupAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (_startupChecked) return;
        _startupChecked = true;
        if (enabled) await CheckAsync(cancellationToken);
    }

    public void DismissNotice() { _noticeDismissed = true; Publish(); }
    public void CancelInstallation() { if (CanCancelInstall) _installation?.Cancel(); }

    /// <summary>Explicit or startup check; never downloads or installs.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCheck || _installed is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _busy = true; _candidate = null; _noticeDismissed = false; StatusMessage = Text("UpdatesChecking"); Publish();
        try
        {
            var result = await _discovery.CheckAsync(_installed, linked.Token);
            if (_closed) return;
            if (result.Status == ReleaseCheckStatus.Available)
            {
                var candidate = result.Candidate;
                if (candidate is null || candidate.Version.CompareTo(_installed) <= 0 ||
                    !_installed.IsPrerelease && candidate.Version.IsPrerelease ||
                    !ReleaseSource.TryValidatePage(candidate.Page, out var page) ||
                    !ReleaseVersion.TryParse(Uri.UnescapeDataString(page!.Segments[^1]), out var pageVersion) ||
                    pageVersion!.CompareTo(candidate.Version) != 0)
                    StatusMessage = Text("UpdatesInvalidData");
                else
                {
                    _candidate = candidate with { Page = page! };
                    StatusMessage = string.Format(CultureInfo.CurrentCulture, Text(candidate.Installer is null && _installer is not null ? "UpdatesManualDownload" : "UpdatesAvailable"), candidate.Version.Display);
                }
            }
            else StatusMessage = Text(result.Status switch
            {
                ReleaseCheckStatus.UpToDate => "UpdatesUpToDate", ReleaseCheckStatus.NoReleases => "UpdatesNoReleases",
                ReleaseCheckStatus.Offline => "UpdatesOffline", ReleaseCheckStatus.TimedOut => "UpdatesTimedOut",
                ReleaseCheckStatus.RateLimited => "UpdatesRateLimited", ReleaseCheckStatus.InvalidData => "UpdatesInvalidData",
                _ => "UpdatesUnavailable"
            });
        }
        catch (OperationCanceledException) { if (!_closed) StatusMessage = Text("UpdatesCanceled"); }
        catch (Exception) { if (!_closed) StatusMessage = Text("UpdatesUnavailable"); }
        finally { _busy = false; if (!_closed) Publish(); }
    }

    /// <summary>Separate user intent from the network check. Revalidates immediately before launching.</summary>
    public async Task OpenReleasePageAsync(CancellationToken cancellationToken = default)
    {
        if (!CanOpen || _candidate is null || !ReleaseSource.TryValidatePage(_candidate.Page, out var page)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _busy = true; Publish();
        try
        {
            if (!await _launcher.OpenAsync(page!, linked.Token) && !_closed) StatusMessage = Text("UpdatesOpenFailed");
        }
        catch (OperationCanceledException) { if (!_closed) StatusMessage = Text("UpdatesCanceled"); }
        catch (Exception) { if (!_closed) StatusMessage = Text("UpdatesOpenFailed"); }
        finally { _busy = false; if (!_closed) Publish(); }
    }

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        if (!CanInstall || _candidate is null || _installer is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _installation = linked; _busy = true; DownloadPercent = 0;
        StatusMessage = Text("UpdatesDownloading"); Publish();
        try
        {
            var progress = new Progress<int>(percent =>
            {
                if (_closed || _installation != linked || linked.IsCancellationRequested) return;
                DownloadPercent = percent;
                StatusMessage = string.Format(CultureInfo.CurrentCulture, Text("UpdatesDownloadProgress"), percent);
                Publish();
            });
            var result = await _installer.InstallAsync(_candidate, progress, linked.Token);
            if (_closed) return;
            StatusMessage = Text(result switch
            {
                ReleaseInstallStatus.Started => "UpdatesInstallerStarted",
                ReleaseInstallStatus.InvalidData => "UpdatesIntegrityFailed",
                ReleaseInstallStatus.Offline => "UpdatesOffline",
                ReleaseInstallStatus.TimedOut => "UpdatesDownloadTimedOut",
                ReleaseInstallStatus.Busy => "UpdatesFinishWork",
                _ => "UpdatesInstallFailed"
            });
            if (result == ReleaseInstallStatus.Started) InstallerStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { if (!_closed) StatusMessage = Text("UpdatesCanceled"); }
        catch (Exception) { if (!_closed) StatusMessage = Text("UpdatesInstallFailed"); }
        finally { _installation = null; _busy = false; if (!_closed) Publish(); }
    }

    private static string Text(string key) => Strings.GetString(key);
    private void Publish()
    {
        foreach (string property in new[] { nameof(StatusMessage), nameof(IsBusy), nameof(CanCheck), nameof(CanOpen), nameof(CanInstall), nameof(IsInstalling), nameof(CanCancelInstall), nameof(ShowNotice), nameof(NoticeMessage), nameof(DownloadPercent) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    public void Dispose()
    {
        if (_closed) return;
        _closed = true; _lifetime.Cancel(); _lifetime.Dispose();
    }
}
