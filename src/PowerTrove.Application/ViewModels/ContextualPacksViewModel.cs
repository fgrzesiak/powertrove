using System.ComponentModel;
using System.Globalization;
using PowerTrove.Application.Packs;
using PowerTrove.Application.Resources;
using PowerTrove.Core.Catalog;
using PowerTrove.Core.Search;

namespace PowerTrove.Application.ViewModels;

public sealed class PackSuggestion : INotifyPropertyChanged
{
    internal PackSuggestion(CatalogAction action, int count, PackInstallOffer? updated = null)
        : this(updated ?? new(action.PackId, action.PackVersion, action.PackName, action.Publisher, action.DownloadBytes,
            action.ManifestFingerprint ?? string.Empty), count, action.InstallState == PackInstallState.UpdateAvailable, action.InstallState == PackInstallState.Incompatible) { }
    internal PackSuggestion(PackInstallOffer offer, int count, bool isUpdate, bool incompatible = false)
    {
        Id = offer.Id; Offer = offer; ActionCount = count; IsUpdate = isUpdate; IsIncompatible = incompatible;
        Title = string.Format(CultureInfo.CurrentCulture, Text("ContextPackOfficial"), Offer.Name);
        Description = incompatible ? Text("PacksIncompatible") : string.Format(CultureInfo.CurrentCulture, Text("ContextPackActions"), count);
        DownloadText = incompatible ? Text("ContextPackIncompatible") : string.Format(CultureInfo.CurrentCulture, Text(IsUpdate ? "ContextPackUpdate" : "ContextPackDownload"),
            Offer.Name, ActionPackRow.FormatSize(Offer.DownloadBytes));
    }
    internal int ActionCount { get; }
    public string Id { get; }
    internal PackInstallOffer Offer { get; }
    public bool IsUpdate { get; }
    public bool IsIncompatible { get; }
    public string Title { get; }
    public string Description { get; }
    public string DownloadText { get; }
    public bool CanDownload { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void SetEnabled(bool value)
    { if (value != CanDownload) { CanDownload = value; PropertyChanged?.Invoke(this, new(nameof(CanDownload))); } }
    private static string Text(string key) => Strings.GetString(key);
}

/// <summary>Local context matching and explicit installation. Never captures context, sends input or executes an action.</summary>
public sealed class ContextualPacksViewModel : INotifyPropertyChanged, IDisposable
{
    private IPackManagementService? _service;
    private IPackManagementNotifications? _notifications;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _active;
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PackInstallOffer> _updated = new(StringComparer.Ordinal);
    private bool _closed, _busy, _inputEnabled = true;
    private Guid? _invocation;
    public IReadOnlyList<PackSuggestion> Suggestions { get; private set; } = [];
    public string StatusMessage { get; private set; } = string.Empty;
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool IsBusy => _busy || _notifications?.Activity is not null;
    public bool CanCancel => IsBusy;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Configure(IPackManagementService service)
    {
        if (_notifications is not null) _notifications.ActivityChanged -= ActivityChanged;
        _service = service;
        _notifications = service as IPackManagementNotifications;
        if (_notifications is not null) _notifications.ActivityChanged += ActivityChanged;
        Publish();
    }
    public void SetResults(ContextSnapshot? snapshot, IReadOnlyList<ActionSearchResult> results, bool showMissing)
    {
        if (_closed) return;
        if (_invocation != snapshot?.InvocationId)
        { _invocation = snapshot?.InvocationId; if (!IsBusy) StatusMessage = string.Empty; }
        Suggestions = !showMissing || snapshot is not { Status: ContextStatus.Available } || snapshot.IsStale ? [] :
            results.Where(r => r.Action is CatalogAction catalog && (r.IsCompatible || catalog.InstallState == PackInstallState.Incompatible && catalog.MatchSelection(snapshot).IsAvailable))
                .GroupBy(r => ((CatalogAction)r.Action).PackId, StringComparer.Ordinal)
                .Where(g => !_dismissed.Contains(g.Key)).Take(3)
                .Select(g => new PackSuggestion((CatalogAction)g.First().Action, g.Count(), _updated.GetValueOrDefault(g.Key))).ToArray();
        if (HasSuggestions && _service is IPackDownloadAvailability { IsDownloadConfigured: false } && !IsBusy)
            StatusMessage = Text("PacksNotConfigured");
        Publish();
    }
    public void SetInputEnabled(bool enabled) { _inputEnabled = enabled; Publish(); }
    public void Dismiss(string id)
    {
        _dismissed.Add(id);
        Suggestions = Suggestions.Where(s => s.Id != id).ToArray();
        Publish();
    }
    public async Task InstallAsync(PackSuggestion suggestion, CancellationToken token = default)
    {
        if (_closed || !suggestion.CanDownload || !Suggestions.Contains(suggestion) || _service is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        _active = linked; _busy = true; StatusMessage = Text("PacksRefreshing"); Publish();
        try
        {
            var prepared = await _service.PrepareAsync(suggestion.Id, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            if (prepared.Status != PackChannelStatus.Ready || prepared.Offer is not { } offer)
            { StatusMessage = Message(prepared.Status); return; }
            var accepted = suggestion.Offer;
            if (offer.Id != accepted.Id) { StatusMessage = Text("PacksOperationFailed"); return; }
            if (offer.Version != accepted.Version || offer.Name != accepted.Name || offer.Publisher != accepted.Publisher ||
                offer.DownloadBytes != accepted.DownloadBytes || accepted.ManifestFingerprint.Length > 0 && offer.ManifestFingerprint != accepted.ManifestFingerprint)
            {
                _updated[suggestion.Id] = offer;
                // A changed catalog requires a new informed click on the updated card.
                Suggestions = Suggestions.Select(row => row.Id == suggestion.Id ? Reoffer(suggestion, offer) : row).ToArray();
                StatusMessage = Text("ContextPackOfferChanged");
                return;
            }
            StatusMessage = Text("PacksInstalling"); Publish();
            var progress = new Progress<PackDownloadProgress>(value =>
            {
                if (_closed || _active != linked) return;
                StatusMessage = Progress(value); Publish();
            });
            var result = await _service.InstallAsync(offer, progress, linked.Token);
            if (_closed) return;
            StatusMessage = Message(result);
            if (result == PackChannelStatus.Installed)
            { _updated.Remove(suggestion.Id); Suggestions = Suggestions.Where(s => s.Id != suggestion.Id).ToArray(); }
        }
        catch (OperationCanceledException) { if (!_closed) StatusMessage = Text("PacksCanceled"); }
        catch (Exception) { if (!_closed) StatusMessage = Text("PacksOperationFailed"); }
        finally { _active = null; _busy = false; if (!_closed) Publish(); }
    }
    private static PackSuggestion Reoffer(PackSuggestion suggestion, PackInstallOffer offer) =>
        new(offer, suggestion.ActionCount, suggestion.IsUpdate);
    public void Cancel() { _active?.Cancel(); _notifications?.Cancel(); }
    private void ActivityChanged(object? sender, EventArgs args) => Dispatch(() =>
    {
        if (_notifications?.Activity is { CompletedBytes: > 0 } active)
            StatusMessage = Progress(new(active.CompletedBytes, active.TotalBytes));
        Publish();
    });
    private static string Progress(PackDownloadProgress value) => value.TotalBytes is > 0
        ? string.Format(CultureInfo.CurrentCulture, Text("ContextPackProgress"), Math.Clamp(value.CompletedBytes * 100d / value.TotalBytes.Value, 0, 100))
        : string.Format(CultureInfo.CurrentCulture, Text("PackProgressBytes"), value.CompletedBytes);
    private static string Message(PackChannelStatus status) => Text(status switch
    {
        PackChannelStatus.Installed => "ContextPackReady", PackChannelStatus.Offline => "PacksOffline",
        PackChannelStatus.TimedOut => "PacksTimedOut", PackChannelStatus.Incompatible => "PacksIncompatible",
        PackChannelStatus.NotConfigured => "PacksNotConfigured", PackChannelStatus.Busy => "PacksBusy", _ => "PacksOperationFailed"
    });
    private static string Text(string key) => Strings.GetString(key);
    private void Dispatch(Action callback)
    {
        if (_closed) return;
        if (_context is null || ReferenceEquals(SynchronizationContext.Current, _context)) callback();
        else _context.Post(_ => { if (!_closed) callback(); }, null);
    }
    private void Publish()
    {
        foreach (var row in Suggestions) row.SetEnabled(!row.IsIncompatible && !_closed && _service is not null && !IsBusy && _inputEnabled &&
            (_service is not IPackDownloadAvailability availability || availability.IsDownloadConfigured));
        foreach (string name in new[] { nameof(Suggestions), nameof(HasSuggestions), nameof(StatusMessage), nameof(IsBusy), nameof(CanCancel) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public void Dispose()
    {
        if (_closed) return; _closed = true;
        if (_notifications is not null) _notifications.ActivityChanged -= ActivityChanged;
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}
