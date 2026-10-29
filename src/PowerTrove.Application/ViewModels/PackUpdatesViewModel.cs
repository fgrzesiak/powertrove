using System.ComponentModel;
using System.Globalization;
using PowerTrove.Application.Packs;
using PowerTrove.Application.Resources;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Application.ViewModels;

/// <summary>Version-aware in-app notices. Automatic work reads signed metadata only.</summary>
public sealed class PackUpdatesViewModel(TimeProvider? clock = null) : INotifyPropertyChanged, IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private IPackManagementService? _service;
    private IPackUpdateChecks? _checks;
    private IPackManagementNotifications? _notifications;
    private CancellationTokenSource? _active;
    private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;
    private int _count, _preferenceRevision, _running;
    private bool _enabled, _closed, _dismissed;
    private string _identity = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    public int UpdateCount { get { lock (_gate) return _count; } }
    public bool ShowNotice { get { lock (_gate) return !_closed && !_dismissed && _count > 0; } }
    public string NoticeMessage
    {
        get
        {
            lock (_gate)
                return _closed || _dismissed || _count == 0 ? string.Empty : string.Format(CultureInfo.CurrentCulture,
                    Strings.GetString(_count == 1 ? "PackUpdatesNoticeOne" : "PackUpdatesNoticeOther"), _count);
        }
    }

    public void Configure(IPackManagementService service, bool enabled)
    {
        lock (_gate)
        {
            if (_closed || _service is not null) throw new InvalidOperationException("Pack update monitoring can only be configured once.");
            _checks = service as IPackUpdateChecks ?? throw new ArgumentException("Pack metadata checks are required.", nameof(service));
            _service = service; _enabled = enabled;
            _notifications = service as IPackManagementNotifications;
            if (_notifications is not null) _notifications.Changed += PacksChanged;
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_closed || _enabled == enabled) return;
            _enabled = enabled; _preferenceRevision++; _nextCheck = DateTimeOffset.MinValue;
            if (!enabled) _active?.Cancel();
        }
    }

    public void DismissNotice() { lock (_gate) _dismissed = true; Publish(); }

    public async Task RunAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("Pack update monitoring is already running.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        try
        {
            await RefreshLocalAsync(linked.Token).ConfigureAwait(false);
            await CheckIfDueAsync(linked.Token).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _clock);
            while (await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false))
                await CheckIfDueAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
    }

    public async Task<PackChannelStatus> CheckIfDueAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate) { if (_closed || !_enabled || _checks is null || _clock.GetUtcNow() < _nextCheck) return PackChannelStatus.Unavailable; }
        if (!await _serial.WaitAsync(0, token).ConfigureAwait(false)) return PackChannelStatus.Busy;
        CancellationTokenSource? active = null; int revision = 0;
        var status = PackChannelStatus.Unavailable;
        try
        {
            lock (_gate)
            {
                if (_closed || !_enabled || _clock.GetUtcNow() < _nextCheck) return status;
                revision = _preferenceRevision;
                active = _active = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            }
            var result = await Task.Run(() => _checks!.CheckForUpdatesAsync(active.Token), active.Token).ConfigureAwait(false);
            status = result.Status;
            lock (_gate)
                if (!_closed && _enabled && revision == _preferenceRevision && status == PackChannelStatus.Ready) Apply(result.Packs);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException) { }
        finally
        {
            lock (_gate)
            {
                if (active is not null && revision == _preferenceRevision && status != PackChannelStatus.Busy)
                    _nextCheck = _clock.GetUtcNow() + (status == PackChannelStatus.Ready ? CheckInterval : RetryInterval);
                _active = null;
            }
            active?.Dispose(); _serial.Release();
        }
        Publish();
        return status;
    }

    public async Task RefreshLocalAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate) { if (_closed || _service is null) return; }
        if (!await _serial.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            var snapshot = await Task.Run(() => _service!.GetLocalAsync(token), token).ConfigureAwait(false);
            lock (_gate) if (!_closed) Apply(snapshot.Packs);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException) { }
        finally { _serial.Release(); }
        Publish();
    }

    private void Apply(IReadOnlyList<PackListItem> packs)
    {
        var updates = packs.Where(p => p.CanUninstall && p.InstalledVersion is not null && p.CanDownload && p.State == PackInstallState.UpdateAvailable)
            .Select(p => p.Id + "@" + p.AvailableVersion).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        string identity = string.Join('\n', updates);
        if (identity != _identity) { _dismissed = false; _identity = identity; }
        _count = updates.Length;
    }

    private async void PacksChanged(object? sender, EventArgs args)
    {
        CancellationToken token;
        lock (_gate) { if (_closed) return; token = _lifetime.Token; }
        try { await RefreshLocalAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
    private void Publish()
    {
        void Notify() { lock (_gate) { if (_closed) return; } PropertyChanged?.Invoke(this, new(null)); }
        if (_context is not null && SynchronizationContext.Current != _context) _context.Post(_ => Notify(), null);
        else Notify();
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            if (_notifications is not null) _notifications.Changed -= PacksChanged;
            _lifetime.Cancel(); _active?.Cancel();
        }
        _lifetime.Dispose();
    }
}
