using System.ComponentModel;
using System.Globalization;
using ContextBar.Application.Configuration;
using ContextBar.Application.Packs;
using ContextBar.Application.Resources;
using ContextBar.Core.Catalog;

namespace ContextBar.Application.ViewModels;

public sealed class ActionPackRow(PackListItem item) : INotifyPropertyChanged
{
    private bool _busy;
    public PackListItem Item { get; } = item;
    public string Id => Item.Id;
    public string Name => Item.Name;
    public string Publisher => Item.Publisher;
    public string StateText => Strings.GetString("PackState" + Item.State);
    public string SizeText => FormatSize(Item.SizeBytes);
    public static string FormatSize(long bytes) => bytes >= 1048576 ? string.Format(CultureInfo.CurrentCulture, Strings.GetString("PackSizeMegabytes"), bytes / 1048576d)
        : bytes >= 1024 ? string.Format(CultureInfo.CurrentCulture, Strings.GetString("PackSizeKilobytes"), bytes / 1024d)
        : bytes > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.GetString("PackSizeBytes"), bytes) : Strings.GetString("PackSizeUnknown");
    public string Details => string.Join(" · ", Item.State == PackInstallState.UpdateAvailable
        ? string.Format(CultureInfo.CurrentCulture, Strings.GetString("PackVersionUpdate"), Item.InstalledVersion, Item.AvailableVersion)
        : string.Format(CultureInfo.CurrentCulture, Strings.GetString("PackVersion"), Item.InstalledVersion ?? Item.AvailableVersion),
        SizeText, string.Format(CultureInfo.CurrentCulture, Strings.GetString(Item.ActionCount == 1 ? "PackActionCountOne" : "PackActionCountOther"), Item.ActionCount));
    public string AutomationName => string.Format(CultureInfo.CurrentCulture, Strings.GetString("PackRowAutomationName"), Name, StateText, Details);
    public string PrimaryText => Strings.GetString(Item.State == PackInstallState.UpdateAvailable ? "PackUpdate" : "PackInstall");
    public string PrimaryAutomationName => string.Format(CultureInfo.CurrentCulture, Strings.GetString("PackActionAutomationName"), PrimaryText, Name);
    public string UninstallAutomationName => string.Format(CultureInfo.CurrentCulture, Strings.GetString("PackRemoveAutomationName"), Name);
    public bool HasDownloadAction => Item.State is PackInstallState.NotInstalled or PackInstallState.UpdateAvailable;
    public bool HasUninstallAction => Item.CanUninstall;
    public bool CanDownload => !_busy && Item.CanDownload;
    public bool CanUninstall => !_busy && Item.CanUninstall;
    public bool IsBuiltIn => Item.IsBuiltIn;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void SetBusy(bool value)
    {
        _busy = value;
        PropertyChanged?.Invoke(this, new(nameof(CanDownload)));
        PropertyChanged?.Invoke(this, new(nameof(CanUninstall)));
    }
}

public sealed class ActionPacksViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IPackManagementService _service;
    private readonly ISettingsService _settings;
    private readonly Action _visibilityChanged;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _active;
    private bool _busy, _closed, _showMissing;
    private long _operationRevision;
    public IReadOnlyList<ActionPackRow> Rows { get; private set; } = [];
    public string StatusMessage { get; private set; } = Strings.GetString("PacksNotOpened");
    public string ProgressText { get; private set; } = string.Empty;
    public bool CanOperate => !_closed && !_busy;
    public bool IsBusy => _busy;
    public bool CanChangeVisibility => CanOperate && !_settings.IsReadOnly && _settings is IPackVisibilitySettings;
    public bool HasNoPacks => Rows.Count == 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool ShowNotInstalledActions
    {
        get => _showMissing;
        set
        {
            if (value == _showMissing || !CanChangeVisibility) return;
            try
            {
                ((IPackVisibilitySettings)_settings).SetShowNotInstalledActions(value);
                _showMissing = value; _visibilityChanged(); Publish();
            }
            catch (Exception) { StatusMessage = Strings.GetString("PacksOperationFailed"); Publish(); }
        }
    }
    public ActionPacksViewModel(IPackManagementService service, ISettingsService settings, Action? visibilityChanged = null)
    { _service = service; _settings = settings; _visibilityChanged = visibilityChanged ?? (() => { }); _showMissing = settings.Settings.ShowNotInstalledActions; }

    public Task OpenAsync(CancellationToken token = default) => RunAsync(async ct =>
    {
        var snapshot = await _service.OpenOnUserRequestAsync(ct);
        SetRows(snapshot.Packs); return snapshot.Status;
    }, "PacksRefreshing", token, reload: false);
    public async Task<PackInstallOffer?> PrepareInstallAsync(string id, CancellationToken token = default)
    {
        PackInstallOffer? offer = null;
        await RunAsync(async ct => { var result = await _service.PrepareAsync(id, ct); offer = result.Offer; return result.Status; }, "PacksRefreshing", token, reload: false);
        return offer;
    }
    public Task InstallAsync(PackInstallOffer offer, CancellationToken token = default)
    {
        if (Rows.SingleOrDefault(r => r.Id == offer.Id)?.CanDownload != true) return Task.CompletedTask;
        return RunAsync(ct =>
        {
            long revision = _operationRevision;
            // Capture the UI context, and discard callbacks from a completed earlier operation.
            var progress = new Progress<PackDownloadProgress>(value =>
            {
                if (_closed || !_busy || revision != _operationRevision) return;
                ProgressText = string.Format(CultureInfo.CurrentCulture, Strings.GetString("PackProgressBytes"), value.CompletedBytes);
                PropertyChanged?.Invoke(this, new(nameof(ProgressText)));
            });
            return _service.InstallAsync(offer, progress, ct);
        }, "PacksInstalling", token);
    }
    public Task InstallFileAsync(string path, CancellationToken token = default) => RunAsync(ct => _service.InstallFileAsync(path, ct), "PacksInstalling", token);
    public Task UninstallAsync(string id, CancellationToken token = default)
    {
        if (Rows.SingleOrDefault(r => r.Id == id)?.CanUninstall != true) return Task.CompletedTask;
        return RunAsync(ct => _service.UninstallAsync(id, ct), "PacksRemoving", token);
    }
    public void Cancel() => _active?.Cancel();
    public void RefreshPreference()
    {
        if (_closed) return;
        _showMissing = _settings.Settings.ShowNotInstalledActions;
        _visibilityChanged(); Publish();
    }

    private async Task RunAsync(Func<CancellationToken, Task<PackChannelStatus>> operation, string message, CancellationToken token, bool reload = true)
    {
        if (!CanOperate) return;
        _operationRevision++;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        _active = linked; _busy = true; ProgressText = string.Empty; StatusMessage = Strings.GetString(message); Publish();
        try
        {
            var result = await operation(linked.Token);
            if (_closed) return;
            if (reload) SetRows((await _service.GetLocalAsync(_lifetime.Token)).Packs);
            StatusMessage = Strings.GetString(result switch
            {
                PackChannelStatus.Ready => "PacksReady", PackChannelStatus.Installed => "PacksInstalled",
                PackChannelStatus.NotConfigured => "PacksNotConfigured", PackChannelStatus.Offline => "PacksOffline",
                PackChannelStatus.TimedOut => "PacksTimedOut", PackChannelStatus.InvalidData => "PacksInvalidData",
                PackChannelStatus.Incompatible => "PacksIncompatible", PackChannelStatus.Busy => "PacksBusy", _ => "PacksOperationFailed"
            });
        }
        catch (OperationCanceledException) { if (!_closed) StatusMessage = Strings.GetString("PacksCanceled"); }
        catch (Exception) { if (!_closed) StatusMessage = Strings.GetString("PacksOperationFailed"); }
        finally { _active = null; _busy = false; if (!_closed) Publish(); }
    }
    private void SetRows(IReadOnlyList<PackListItem> rows) => Rows = rows.Select(item => new ActionPackRow(item)).ToArray();
    private void Publish()
    {
        foreach (var row in Rows) row.SetBusy(_busy);
        foreach (string property in new[] { nameof(Rows), nameof(StatusMessage), nameof(ProgressText), nameof(IsBusy), nameof(CanOperate),
            nameof(CanChangeVisibility), nameof(HasNoPacks), nameof(ShowNotInstalledActions) }) PropertyChanged?.Invoke(this, new(property));
    }
    public void Dispose()
    {
        if (_closed) return;
        _closed = true; _lifetime.Cancel(); _lifetime.Dispose();
    }
}
