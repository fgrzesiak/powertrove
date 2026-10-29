namespace PowerTrove.Application.Packs;

public sealed record PackOperation(string? PackId, long CompletedBytes = 0, long? TotalBytes = null);

/// <summary>Application-wide notifications; subscribers marshal to their own UI context.</summary>
public interface IPackManagementNotifications
{
    event EventHandler? Changed;
    event EventHandler? ActivityChanged;
    PackOperation? Activity { get; }
    void Cancel();
}

/// <summary>One cancellable operation shared by every entry point. A view closing does not own other views' work.</summary>
public sealed class CoordinatedPackManagement(IPackManagementService inner, Func<CancellationToken, Task<IAsyncDisposable>>? acquireMutation = null) : IPackManagementService, IPackManagementNotifications, IPackDownloadAvailability, IPackUpdateChecks, IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _active;
    private Task<PackChannelStatus>? _installation;
    private PackInstallOffer? _offer;
    private PackOperation? _activity;
    private bool _closed;
    public bool IsDownloadConfigured => inner is not IPackDownloadAvailability configured || configured.IsDownloadConfigured;
    public event EventHandler? Changed;
    public event EventHandler? ActivityChanged;
    public PackOperation? Activity { get { lock (_gate) return _activity; } }

    public Task<PackPageSnapshot> GetLocalAsync(CancellationToken token) => Task.Run(() => inner.GetLocalAsync(token), token);
    public async Task<PackPageSnapshot> CheckForUpdatesAsync(CancellationToken token)
    {
        if (inner is not IPackUpdateChecks checks) return new(PackChannelStatus.NotConfigured, []);
        using var operation = Begin(null, token);
        if (operation is null) return new(PackChannelStatus.Busy, []);
        var result = await Task.Run(() => checks.CheckForUpdatesAsync(operation.Token), operation.Token).ConfigureAwait(false);
        if (result.Status == PackChannelStatus.Ready) Notify(Changed);
        return result;
    }
    public async Task<PackPageSnapshot> OpenOnUserRequestAsync(CancellationToken token)
    {
        using var operation = Begin(null, token);
        if (operation is null) return new(PackChannelStatus.Busy, (await inner.GetLocalAsync(token).ConfigureAwait(false)).Packs);
        var result = await Task.Run(() => inner.OpenOnUserRequestAsync(operation.Token), operation.Token).ConfigureAwait(false);
        Notify(Changed);
        return result;
    }
    public async Task<PackOfferResult> PrepareAsync(string id, CancellationToken token)
    {
        using var operation = Begin(id, token);
        if (operation is null) return new(PackChannelStatus.Busy);
        var result = await Task.Run(() => inner.PrepareAsync(id, operation.Token), operation.Token).ConfigureAwait(false);
        Notify(Changed);
        return result;
    }
    public Task<PackChannelStatus> InstallAsync(PackInstallOffer offer, IProgress<PackDownloadProgress>? progress, CancellationToken token)
    {
        TaskCompletionSource<PackChannelStatus> completion;
        Operation? operation;
        lock (_gate)
        {
            if (_installation is not null && _offer == offer) return _installation.WaitAsync(token);
            operation = Begin(offer.Id, token);
            if (operation is null) return Task.FromResult(PackChannelStatus.Busy);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _offer = offer;
            _installation = completion.Task;
        }
        _ = CompleteInstallAsync(offer, progress, operation, completion);
        return completion.Task;
    }
    private async Task CompleteInstallAsync(PackInstallOffer offer, IProgress<PackDownloadProgress>? progress,
        Operation operation, TaskCompletionSource<PackChannelStatus> completion)
    {
        PackChannelStatus? result = null;
        Exception? failure = null;
        try
        {
            await using (var mutation = acquireMutation is null ? null : await acquireMutation(operation.Token).ConfigureAwait(false))
            {
                result = await inner.InstallAsync(offer, new DownloadProgress(value =>
                {
                    lock (_gate) { if (_active == operation.Source) _activity = new(offer.Id, value.CompletedBytes, value.TotalBytes); }
                    Notify(ActivityChanged);
                    progress?.Report(value);
                }), operation.Token).ConfigureAwait(false);
            }
            if (result == PackChannelStatus.Installed) Notify(Changed);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_installation, completion.Task)) { _installation = null; _offer = null; } }
            operation.Dispose();
        }
        if (failure is OperationCanceledException) completion.TrySetCanceled();
        else if (failure is not null) completion.TrySetException(failure);
        else completion.TrySetResult(result!.Value);
    }
    public async Task<PackChannelStatus> InstallFileAsync(string path, CancellationToken token)
    {
        using var operation = Begin(null, token);
        if (operation is null) return PackChannelStatus.Busy;
        await using var mutation = acquireMutation is null ? null : await acquireMutation(operation.Token).ConfigureAwait(false);
        var result = await Task.Run(() => inner.InstallFileAsync(path, operation.Token), operation.Token).ConfigureAwait(false);
        if (result == PackChannelStatus.Installed) Notify(Changed);
        return result;
    }
    public async Task<PackChannelStatus> UninstallAsync(string id, CancellationToken token)
    {
        using var operation = Begin(id, token);
        if (operation is null) return PackChannelStatus.Busy;
        await using var mutation = acquireMutation is null ? null : await acquireMutation(operation.Token).ConfigureAwait(false);
        var result = await Task.Run(() => inner.UninstallAsync(id, operation.Token), operation.Token).ConfigureAwait(false);
        if (result == PackChannelStatus.Ready) Notify(Changed);
        return result;
    }
    private void Notify(EventHandler? handlers)
    {
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); }
            catch (Exception) { /* An observer cannot undo a committed installation or strand a shared completion. */ }
    }
    private Operation? Begin(string? id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Operation operation;
        lock (_gate)
        {
            if (_closed || _active is not null) return null;
            _active = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            _activity = new(id);
            operation = new(this, _active);
        }
        Notify(ActivityChanged);
        return operation;
    }
    private void End(CancellationTokenSource source)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, source)) return;
            _active = null; _activity = null;
            source.Dispose();
        }
        Notify(ActivityChanged);
    }
    public void Cancel() { lock (_gate) _active?.Cancel(); }
    public void Dispose()
    {
        lock (_gate) { if (_closed) return; _closed = true; _lifetime.Cancel(); }
        _lifetime.Dispose();
    }
    private sealed class Operation(CoordinatedPackManagement owner, CancellationTokenSource source) : IDisposable
    {
        public CancellationTokenSource Source => source;
        public CancellationToken Token => source.Token;
        public void Dispose() => owner.End(source);
    }
    private sealed class DownloadProgress(Action<PackDownloadProgress> report) : IProgress<PackDownloadProgress>
    { public void Report(PackDownloadProgress value) => report(value); }
}
