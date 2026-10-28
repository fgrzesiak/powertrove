using System.Collections.Concurrent;
using PowerTrove.Core.Extensibility.PackProtocol;

namespace PowerTrove.Application.Packs;

/// <summary>Lazy, serialized workers with owned staging, bounded cancellation and crash backoff.</summary>
public sealed class PackHostSupervisor : IAsyncDisposable
{
    private readonly IPackWorkerProcessFactory _factory;
    private readonly string _workerRoot, _stagingRoot;
    private readonly TimeProvider _clock;
    private readonly PackOutputCommitter _committer;
    private readonly ConcurrentDictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PackWorkerGroup> _bindings = new(StringComparer.Ordinal);

    // Binding happens when actions are registered, including groups that have never started.
    // A fresh registration after maintenance gets a new identity; old action references stay retired.
    internal PackWorkerGroup BindGroup(PackWorkerGroup definition)
    {
        var bound = _bindings.AddOrUpdate(definition.Id, _ => definition, (_, current) =>
            _groups.TryGetValue(current.Id, out var group) && group.Retired
                ? definition with { Id = definition.Id + "#" + Guid.NewGuid().ToString("N") } : current);
        var registered = _groups.GetOrAdd(bound.Id, _ => new Group(bound, _clock.GetUtcNow()));
        if (!SameDefinition(registered.Definition, definition)) throw new InvalidOperationException("A registered pack payload cannot change without maintenance.");
        return bound;
    }
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private int _mutating;
    private readonly CancellationTokenSource _lifetime = new();

    public PackHostSupervisor(IPackWorkerProcessFactory factory, string userRoot, TimeProvider? clock = null,
        PackOutputCommitter? committer = null)
    {
        _factory = factory; _clock = clock ?? TimeProvider.System; _committer = committer ?? new();
        userRoot = PackOutputCommitter.Canonical(userRoot); PackOutputCommitter.RejectReparse(userRoot);
        _workerRoot = Path.Combine(userRoot, "workers"); _stagingRoot = Path.Combine(userRoot, "staging");
    }

    public async ValueTask<ActionPreview> PreviewAsync(PackWorkerGroup definition, string actionId, ContextSnapshot snapshot,
        ActionParameters parameters, CancellationToken token)
    {
        var result = await RequestAsync(definition, actionId, snapshot, parameters, null, token, execute: false).ConfigureAwait(false);
        return result is PreviewResultMessage preview ? preview.ToPreview()
            : new ActionPreview(false, PackStrings.Stopped, Array.Empty<string>(), Array.Empty<string>(), IsBlocked: true);
    }

    public async Task<ActionReceipt> ExecuteAsync(PackWorkerGroup definition, string actionId, ContextSnapshot snapshot,
        ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken token)
    {
        try
        {
            var result = await RequestAsync(definition, actionId, snapshot, parameters, progress, token, execute: true).ConfigureAwait(false);
            return result is CommittedMessage committed ? committed.Receipt : Failed();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(false, true, Array.Empty<string>(), Array.Empty<ActionFailure>(), false); }
    }

    private async Task<PackMessage?> RequestAsync(PackWorkerGroup definition, string actionId, ContextSnapshot snapshot,
        ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken token, bool execute)
    {
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        if (snapshot.IsStale || snapshot.Status != ContextStatus.Available || snapshot.Content is not FileSelection)
            return new ErrorMessage("invalidRequest", PackStrings.InvalidSelection);
        using var executionLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _executionGate.WaitAsync(executionLifetime.Token).ConfigureAwait(false);
        await using var executionLease = new ExecutionLease(_executionGate);
        if (snapshot.IsStale) return new ErrorMessage("invalidRequest", PackStrings.InvalidSelection);
        var selection = FileSelectionPayload.FromSnapshot(snapshot);
        var values = parameters switch
        {
            DictionaryParameters dictionary => dictionary.Values,
            EmptyParameters => new Dictionary<string, string>(),
            _ => throw new ArgumentException("Remote actions require declarative parameters.", nameof(parameters))
        };
        ValidateGroup(definition);
        var group = _groups.GetOrAdd(definition.Id, _ => new Group(definition, _clock.GetUtcNow()));
        if (!SameDefinition(group.Definition, definition)) throw new InvalidOperationException("Stop the previous pack group before replacing its payload.");
        using var queueLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await group.Gate.WaitAsync(queueLifetime.Token).ConfigureAwait(false);
        string? staging = null;
        using var active = CancellationTokenSource.CreateLinkedTokenSource(queueLifetime.Token);
        group.ActiveRequest = active;
        try
        {
            if (group.Stopping || group.Retired) return null;
            token.ThrowIfCancellationRequested();
            await StartAsync(group, active.Token).ConfigureAwait(false);
            group.LastUsed = _clock.GetUtcNow();
            Guid requestId = Guid.NewGuid();
            PackMessage request;
            if (execute)
            {
                staging = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
                PackOutputCommitter.RejectReparse(staging); Directory.CreateDirectory(staging);
                request = new ExecuteMessage(requestId, actionId, selection, values, snapshot.ExpiresAt, staging);
            }
            else request = new PreviewMessage(requestId, actionId, selection, values, snapshot.ExpiresAt);
            await WriteAsync(group, request, TimeSpan.FromSeconds(10), _lifetime.Token).ConfigureAwait(false);
            PackMessage response = await ResponseAsync(group, requestId, execute, progress, active.Token).ConfigureAwait(false);
            if (response is ResultMessage result)
            {
                // Already completed staged outputs may be retained after cooperative cancellation.
                if (token.IsCancellationRequested) result = result with { Receipt = result.Receipt with { IsCanceled = true, IsSuccess = false } };
                var receipt = await _committer.CommitAsync(staging!, selection, result, _lifetime.Token).ConfigureAwait(false);
                try { await CleanStagingAsync(group, staging!).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Preserve the real committed outputs even if OS cleanup fails after the commit.
                    receipt = receipt with { IsSuccess = false, Message = PackStrings.OutputFailed,
                        Failures = receipt.Failures.Append(new ActionFailure(null, PackStrings.OutputFailed)).ToArray() };
                }
                staging = null;
                group.Failures = 0;
                return new CommittedMessage(receipt);
            }
            group.Failures = 0;
            return response;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            // Includes cancel without a result: stop before deleting staging so a native writer cannot recreate it.
            await StopCoreAsync(group).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await StopCoreAsync(group).ConfigureAwait(false);
            group.Failures = Math.Min(group.Failures + 1, 6);
            group.NextStart = _clock.GetUtcNow().AddSeconds(Math.Min(30, 1 << (group.Failures - 1)));
            return null;
        }
        finally
        {
            try { if (staging is not null) await CleanStagingAsync(group, staging).ConfigureAwait(false); }
            finally { group.ActiveRequest = null; group.LastUsed = _clock.GetUtcNow(); group.Gate.Release(); }
        }
    }

    private async Task StartAsync(Group group, CancellationToken token)
    {
        if (group.Process is not null) return;
        var delay = group.NextStart - _clock.GetUtcNow();
        if (delay > TimeSpan.Zero) await Task.Delay(delay, _clock, token).ConfigureAwait(false);
        group.Work = Path.Combine(_workerRoot, Guid.NewGuid().ToString("N"));
        PackOutputCommitter.RejectReparse(_workerRoot); PackOutputCommitter.RejectReparse(_stagingRoot);
        Directory.CreateDirectory(_workerRoot); Directory.CreateDirectory(_stagingRoot);
        PackOutputCommitter.RejectReparse(group.Work); Directory.CreateDirectory(group.Work);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), _clock);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        // Factories own every process they create until returning it and must kill it on cancellation.
        group.Process = await _factory.StartAsync(group.Definition, group.Work, startup.Token).ConfigureAwait(false);
        group.Writer = new PackMessageWriter(group.Process.Input);
        group.Reader = PackMessageStream.ReadAsync(group.Process.Output, _lifetime.Token).GetAsyncEnumerator(_lifetime.Token);
        await WriteAsync(group, new HelloMessage(1, PackDistributionConfiguration.HostVersion.ToString()), TimeSpan.FromSeconds(10), startup.Token).ConfigureAwait(false);
        group.PendingRead = group.Reader.MoveNextAsync().AsTask();
        bool available = await group.PendingRead.WaitAsync(startup.Token).ConfigureAwait(false);
        group.PendingRead = null;
        if (!available
            || group.Reader.Current is not ReadyMessage ready || ready.Packs.Count != group.Definition.Packs.Count
            || ready.Packs.Any(p => !group.Definition.Packs.Contains(new PackWorkerIdentity(p.Id, p.Version))))
            throw new IOException("Worker handshake failed.");
        group.IdleMonitor ??= IdleMonitorAsync(group);
    }

    private async Task<PackMessage> ResponseAsync(Group group, Guid requestId, bool execute,
        IProgress<ActionProgress>? progress, CancellationToken token)
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => canceled.TrySetResult());
        DateTimeOffset? cancelDeadline = null;
        while (true)
        {
            if (cancelDeadline is { } elapsed && elapsed <= _clock.GetUtcNow()) throw new OperationCanceledException(token);
            var read = group.PendingRead = group.Reader!.MoveNextAsync().AsTask();
            if (cancelDeadline is null && await Task.WhenAny(read, canceled.Task)
                .WaitAsync(TimeSpan.FromSeconds(execute ? 15 : 120), _clock, _lifetime.Token).ConfigureAwait(false) == canceled.Task)
            {
                await WriteAsync(group, new CancelMessage(requestId), TimeSpan.FromSeconds(1), _lifetime.Token).ConfigureAwait(false);
                cancelDeadline = _clock.GetUtcNow().AddSeconds(5);
            }
            TimeSpan timeout = cancelDeadline is { } deadline ? deadline - _clock.GetUtcNow() : TimeSpan.FromSeconds(execute ? 15 : 120);
            bool available;
            try { available = await read.WaitAsync(timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero, _clock, _lifetime.Token).ConfigureAwait(false); }
            catch (TimeoutException) when (cancelDeadline is not null) { throw new OperationCanceledException(token); }
            if (!available) throw new IOException("Worker exited.");
            group.PendingRead = null;
            switch (group.Reader.Current)
            {
                case ProgressMessage p when p.RequestId == requestId && execute:
                    progress?.Report(new ActionProgress(p.Fraction)); break;
                case HeartbeatMessage h when h.RequestId == requestId: break;
                case ResultMessage result when result.RequestId == requestId && execute: return result;
                case PreviewResultMessage preview when preview.RequestId == requestId && !execute: return preview;
                case ErrorMessage error when error.RequestId == requestId: return error;
                default: throw new IOException("Unexpected worker response.");
            }
        }
    }

    private async Task IdleMonitorAsync(Group group)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _clock, _lifetime.Token).ConfigureAwait(false);
                if (!await group.Gate.WaitAsync(0, _lifetime.Token).ConfigureAwait(false)) continue;
                try { if (_clock.GetUtcNow() - group.LastUsed >= TimeSpan.FromMinutes(2)) await StopCoreAsync(group).ConfigureAwait(false); }
                finally { group.Gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public async Task StopAsync(string groupId, CancellationToken token = default)
    {
        if (!_groups.TryGetValue(groupId, out var group)) return;
        group.Stopping = true;
        try { group.ActiveRequest?.Cancel(); } catch (ObjectDisposedException) { }
        try
        {
            await group.Gate.WaitAsync(token).ConfigureAwait(false);
            try { await StopCoreAsync(group).ConfigureAwait(false); }
            finally { group.Gate.Release(); }
        }
        finally { group.Stopping = false; }
        // Keep the synchronization object: replacement definitions are allowed only through a new supervisor/group ID.
    }

    /// <summary>Maintenance waits for active worker requests instead of canceling them. UI remains responsive.</summary>
    public async Task<IAsyncDisposable> AcquireMutationAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _executionGate.WaitAsync(linked.Token).ConfigureAwait(false);
        Interlocked.Exchange(ref _mutating, 1);
        return new ExecutionLease(_executionGate, () => Interlocked.Exchange(ref _mutating, 0));
    }
    private sealed class ExecutionLease(SemaphoreSlim gate, Action? beforeRelease = null) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        { if (Interlocked.Exchange(ref _disposed, 1) == 0) { beforeRelease?.Invoke(); gate.Release(); } return ValueTask.CompletedTask; }
    }

    public async Task StopPackAsync(string packId, CancellationToken token = default)
    {
        foreach (var group in _groups.Values.Where(g => g.Definition.Packs.Any(p => p.Id == packId)))
        {
            await StopAsync(group.Definition.Id, token).ConfigureAwait(false);
            if (Volatile.Read(ref _mutating) != 0) group.Retired = true;
        }
    }

    private async Task StopCoreAsync(Group group)
    {
        if (group.Process is { } process)
        {
            try { await process.KillAsync().ConfigureAwait(false); }
            finally { await process.DisposeAsync().ConfigureAwait(false); group.Process = null; }
        }
        if (group.PendingWrite is { } write)
        {
            try { await write.ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            group.PendingWrite = null;
        }
        // Closing the process streams ends any outstanding protocol read before enumerator disposal.
        if (group.Reader is { } reader)
        {
            if (group.PendingRead is { } pending)
            {
                try { await pending.ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or PackProtocolException) { }
                group.PendingRead = null;
            }
            try { await reader.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or NotSupportedException) { }
            group.Reader = null;
        }
        group.Writer?.Dispose(); group.Writer = null;
        if (group.Work is { } work) { DeleteOwned(work, _workerRoot); group.Work = null; }
    }

    private async Task WriteAsync(Group group, PackMessage message, TimeSpan timeout, CancellationToken token)
    {
        group.PendingWrite = group.Writer!.WriteAsync(message, token);
        await group.PendingWrite.WaitAsync(timeout, _clock, token).ConfigureAwait(false);
        group.PendingWrite = null;
    }

    private async Task CleanStagingAsync(Group group, string path)
    {
        try { DeleteOwned(path, _stagingRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A misbehaving converter may retain a file handle after replying. Kill the contained
            // process before the second cleanup attempt; never delete while native work is alive.
            await StopCoreAsync(group).ConfigureAwait(false);
            DeleteOwned(path, _stagingRoot);
        }
    }

    private static void DeleteOwned(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        if (!Guid.TryParseExact(relative, "N", out _)) throw new InvalidOperationException("Invalid owned directory.");
        // Never follow a reparse point during recursive deletion.
        if (!Directory.Exists(path)) return;
        DeleteTree(path);
    }
    private static void DeleteTree(string path)
    {
        var pending = new Stack<(string Path, bool Visited)>(); pending.Push((path, false));
        while (pending.TryPop(out var item))
        {
            var attributes = File.GetAttributes(item.Path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            { if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(item.Path); else File.Delete(item.Path); }
            else if ((attributes & FileAttributes.Directory) == 0)
            { if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(item.Path, attributes & ~FileAttributes.ReadOnly); File.Delete(item.Path); }
            else if (item.Visited) Directory.Delete(item.Path);
            else
            {
                pending.Push((item.Path, true));
                foreach (string child in Directory.EnumerateFileSystemEntries(item.Path)) pending.Push((child, false));
            }
        }
    }
    private static bool SameDefinition(PackWorkerGroup left, PackWorkerGroup right) => left.PackDirectories.SequenceEqual(right.PackDirectories, StringComparer.OrdinalIgnoreCase)
        && left.Packs.SequenceEqual(right.Packs) && (left.ManifestHashes ?? []).SequenceEqual(right.ManifestHashes ?? [], StringComparer.OrdinalIgnoreCase);
    private static void ValidateGroup(PackWorkerGroup group)
    {
        if (string.IsNullOrWhiteSpace(group.Id) || group.Id.Length > 256 || group.PackDirectories.Count is 0 or > 64 || group.Packs.Count is 0 or > 64
            || group.Packs.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != group.Packs.Count) throw new ArgumentException("Invalid pack group.");
        foreach (string directory in group.PackDirectories) PackOutputCommitter.RejectReparse(PackOutputCommitter.Canonical(directory));
        if (group.ManifestHashes is { } hashes && (hashes.Count != group.Packs.Count
            || hashes.Any(h => h.Length != 64 || h.Any(c => !Uri.IsHexDigit(c))))) throw new ArgumentException("Invalid pinned manifest inventory.");
    }
    private static ActionReceipt Failed() => new(false, false, Array.Empty<string>(), [new ActionFailure(null, PackStrings.Stopped)], false, PackStrings.Stopped);

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        foreach (var group in _groups.Values)
        {
            await group.Gate.WaitAsync().ConfigureAwait(false);
            try { await StopCoreAsync(group).ConfigureAwait(false); }
            finally { group.Gate.Release(); }
            if (group.IdleMonitor is { } monitor) await monitor.ConfigureAwait(false);
        }
        // Keep the canceled source alive for in-flight callers observing shutdown.
    }

    private sealed class Group(PackWorkerGroup definition, DateTimeOffset now)
    {
        public PackWorkerGroup Definition { get; } = definition;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IPackWorkerProcess? Process;
        public PackMessageWriter? Writer;
        public IAsyncEnumerator<PackMessage>? Reader;
        public string? Work;
        public int Failures;
        public DateTimeOffset LastUsed = now, NextStart;
        public Task? IdleMonitor;
        public Task<bool>? PendingRead;
        public Task? PendingWrite;
        public CancellationTokenSource? ActiveRequest;
        public volatile bool Retired;
        public volatile bool Stopping;
    }
    // Internal host-only value, never passed to the wire serializer.
    private sealed record CommittedMessage(ActionReceipt Receipt) : PackMessage
    { public override string Type => "host-committed"; }
}
