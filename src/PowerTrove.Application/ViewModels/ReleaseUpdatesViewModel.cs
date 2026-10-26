using System.ComponentModel;
using System.Globalization;
using PowerTrove.Application.Resources;
using PowerTrove.Application.Updates;

namespace PowerTrove.Application.ViewModels;

public sealed class ReleaseUpdatesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IReleaseDiscovery _discovery;
    private readonly IReleasePageLauncher _launcher;
    private readonly ReleaseVersion? _installed;
    private readonly CancellationTokenSource _lifetime = new();
    private ReleaseCandidate? _candidate;
    private bool _busy, _closed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string InstalledVersionText { get; }
    public string StatusMessage { get; private set; }
    public bool IsBusy => _busy;
    public bool CanCheck => !_closed && !_busy && _installed is not null;
    public bool CanOpen => !_closed && !_busy && _candidate is not null;

    public ReleaseUpdatesViewModel(string? installedVersion, IReleaseDiscovery discovery, IReleasePageLauncher launcher)
    {
        _discovery = discovery; _launcher = launcher;
        ReleaseVersion.TryParse(installedVersion, out _installed);
        InstalledVersionText = _installed is null ? Text("UpdatesVersionUnknown") :
            string.Format(CultureInfo.CurrentCulture, Text("UpdatesInstalledVersion"), _installed.Display);
        StatusMessage = Text(_installed is null ? "UpdatesVersionUnknown" : "UpdatesNotChecked");
    }

    /// <summary>Called only by the explicit Settings check control, never during construction.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCheck || _installed is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _busy = true; _candidate = null; StatusMessage = Text("UpdatesChecking"); Publish();
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
                    StatusMessage = string.Format(CultureInfo.CurrentCulture, Text("UpdatesAvailable"), candidate.Version.Display);
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

    private static string Text(string key) => Strings.GetString(key);
    private void Publish()
    {
        foreach (string property in new[] { nameof(StatusMessage), nameof(IsBusy), nameof(CanCheck), nameof(CanOpen) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    public void Dispose()
    {
        if (_closed) return;
        _closed = true; _lifetime.Cancel(); _lifetime.Dispose();
    }
}
