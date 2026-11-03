using PowerTrove.Abstractions.Packs;
using System.Collections.Concurrent;
using PowerTrove.Core.Extensibility.PackProtocol;

namespace PowerTrove.PackHost;

public sealed class WorkerSession
{
    private readonly IReadOnlyList<IPackFactory> _factories;
    private readonly ConcurrentDictionary<Guid,TaskCompletionSource<BrowserServiceResultMessage>> _browserPending = new();
    private readonly ConcurrentDictionary<Guid,WorkerBrowserBinaryPending> _browserBinaryPending = new();
    private readonly Func<FileActionServices, IPackHostServices> _services;
    private readonly IFileTypeVerifier _verifier;
    private readonly string _stagingRoot;
    private readonly TimeProvider _clock;
    private readonly IReadOnlySet<string>? _declaredActionIds;

    public WorkerSession(IReadOnlyList<IPackFactory> factories, Func<FileActionServices, IPackHostServices> services,
        IFileTypeVerifier verifier, string stagingRoot, TimeProvider? clock = null,
        IReadOnlySet<string>? declaredActionIds = null)
    { _factories = factories; _services = services; _verifier = verifier; _stagingRoot = StagingOutputFileSystem.Canonical(stagingRoot); _clock = clock ?? TimeProvider.System; _declaredActionIds = declaredActionIds; }

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        using var writer = new PackMessageWriter(output);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? request = null;
        Task? work = null;
        Guid? requestId = null;
        bool ready = false;
        try
        {
            await foreach (var message in PackMessageStream.ReadAsync(input, lifetime.Token).ConfigureAwait(false))
            {
                if (message is ShutdownMessage) break;
                if (message is HelloMessage hello && !ready && hello.ProtocolVersion == 1)
                {
                    ready = true;
                    await writer.WriteAsync(new ReadyMessage(_factories.Select(f => new WorkerPack(f.Info.Id, f.Info.Version)).ToArray()), lifetime.Token).ConfigureAwait(false);
                }
                else if (message is CancelMessage cancel && requestId == cancel.RequestId) request?.Cancel();
                else if (message is BrowserServiceResultMessage browser && requestId == browser.RequestId)
                {
                    if (_browserPending.TryRemove(browser.OperationId, out var pending)) pending.TrySetResult(browser);
                    else if (_browserBinaryPending.TryGetValue(browser.OperationId, out var binary))
                        binary.Fail(); // Failed or missing capture: never expose partial bytes.
                }
                else if (message is BrowserServiceBinaryChunkMessage chunk && requestId == chunk.RequestId &&
                    _browserBinaryPending.TryGetValue(chunk.OperationId, out var transfer))
                    transfer.Append(chunk);
                else if (ready && message is PreviewMessage or ExecuteMessage)
                {
                    Guid id = message is PreviewMessage preview ? preview.RequestId : ((ExecuteMessage)message).RequestId;
                    if (work is { IsCompleted: false })
                    { await writer.WriteAsync(new ErrorMessage("invalidRequest", Resources.Strings.Busy, id), lifetime.Token).ConfigureAwait(false); continue; }
                    if (work is not null) await work.ConfigureAwait(false);
                    request?.Dispose();
                    request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    requestId = id;
                    var requestToken = request.Token;
                    work = Task.Run(() => HandleAsync(message, writer, requestToken, lifetime.Token));
                }
                else await writer.WriteAsync(new ErrorMessage("invalidRequest", Resources.Strings.InvalidRequest), lifetime.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            request?.Cancel();
            if (work is not null)
            {
                try { await work.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) { }
            }
            lifetime.Cancel();
            request?.Dispose();
            foreach (var pending in _browserPending.Values) pending.TrySetCanceled();
            _browserPending.Clear();
            foreach (var binary in _browserBinaryPending.Values) binary.Fail();
            _browserBinaryPending.Clear();
        }
    }

    private async Task HandleAsync(PackMessage message, PackMessageWriter writer, CancellationToken token, CancellationToken sessionToken)
    {
        var execute = message as ExecuteMessage;
        var preview = message as PreviewMessage;
        Guid id = execute?.RequestId ?? preview!.RequestId;
        string actionId = execute?.ActionId ?? preview!.ActionId;
        var selection = execute?.Selection ?? preview!.Selection;
        var table = execute is null ? preview!.Table : execute.Table;
        var expires = execute?.ExpiresAt ?? preview!.ExpiresAt;
        var values = execute?.Parameters ?? preview!.Parameters;
        var textInput = execute?.TextInput ?? preview?.TextInput;
        var browserContext = execute?.BrowserContext ?? preview?.BrowserContext;
        using var heartbeatLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken);
        Task? heartbeat = null;
        try
        {
            // Exactly one input kind: files, a table or text.
            if (expires <= _clock.GetUtcNow() || expires > _clock.GetUtcNow().AddMinutes(5) ||
                (selection.Files.Count > 0 ? 1 : 0) + (table is null ? 0 : 1) + (textInput is null ? 0 : 1) != 1)
                throw new InvalidDataException();
            string staging;
            if (execute is not null)
            {
                staging = StagingOutputFileSystem.Canonical(execute.StagingDirectory);
                string relative = Path.GetRelativePath(_stagingRoot, staging);
                if (!Guid.TryParseExact(relative, "N", out _) || relative.Contains(Path.DirectorySeparatorChar)) throw new InvalidDataException();
            }
            else staging = Path.Combine(_stagingRoot, "preview");
            var fs = new StagingOutputFileSystem(selection, staging, readOnly: execute is null);
            var host = _services(new FileActionServices(fs, new OutputPlanner(fs), new SelectionVerifier(fs, _verifier)));
            if (browserContext?.Tab is { } allowedTab && host is PackHostServices typedHost)
                typedHost.Add<IBrowserPageService>(new WorkerBrowserPageProxy(writer,
                    _browserPending, _browserBinaryPending, id, allowedTab));
            var actions = _factories.SelectMany(f => f.Create(host).Actions).ToArray();
            var ids = actions.Select(a => a.Descriptor.Id).ToHashSet(StringComparer.Ordinal);
            if (ids.Count != actions.Length || _declaredActionIds is not null && !_declaredActionIds.SetEquals(ids))
                throw new InvalidDataException();
            var action = actions.SingleOrDefault(a => a.Descriptor.Id == actionId);
            if (action is null)
            { await writer.WriteAsync(new ErrorMessage("unknownAction", Resources.Strings.UnknownAction, id), sessionToken).ConfigureAwait(false); return; }
            var snapshot = table is not null ? Snapshot(table, expires) : textInput is not null ? TextSnapshot(textInput, expires, browserContext) : Snapshot(selection, expires);
            var parameters = new DictionaryParameters(values);
            if (execute is null)
            {
                var result = await action.PreviewAsync(snapshot, parameters, token).ConfigureAwait(false);
                await writer.WriteAsync(PreviewResultMessage.FromPreview(id, result), sessionToken).ConfigureAwait(false);
                return;
            }
            heartbeat = HeartbeatAsync(id, writer, heartbeatLifetime.Token);
            var progress = new InlineProgress(value =>
            {
                // Only counts/fractions are forwarded; CurrentItem and arbitrary engine text never become diagnostics.
                writer.WriteAsync(new ProgressMessage(id, Math.Clamp(double.IsFinite(value.Percentage) ? value.Percentage : 0, 0, 1)), token).GetAwaiter().GetResult();
            });
            var receipt = await action.ExecuteAsync(snapshot, parameters, progress, token).ConfigureAwait(false);
            var outputs = fs.DescribeOutputs(receipt.OutputPaths);
            // Wire paths are staging-relative. The host rewrites them only after its own no-overwrite commit.
            var stagedReceipt = receipt with { OutputPaths = outputs.Select(o => o.Path).ToArray() };
            await writer.WriteAsync(new ResultMessage(id, stagedReceipt, outputs), sessionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await writer.WriteAsync(new ResultMessage(id, new ActionReceipt(false, true, Array.Empty<string>(), Array.Empty<ActionFailure>(), false), Array.Empty<StagedOutput>()), sessionToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await writer.WriteAsync(new ErrorMessage(ex is InvalidDataException or UnauthorizedAccessException ? "invalidRequest" : "internal",
                ex is InvalidDataException or UnauthorizedAccessException ? Resources.Strings.InvalidRequest : Resources.Strings.Failed, id), sessionToken).ConfigureAwait(false);
        }
        finally
        {
            heartbeatLifetime.Cancel();
            if (heartbeat is not null) { try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        }
    }

    private async Task HeartbeatAsync(Guid id, PackMessageWriter writer, CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _clock, token).ConfigureAwait(false);
            await writer.WriteAsync(new HeartbeatMessage(id), token).ConfigureAwait(false);
        }
    }
    private ContextSnapshot TextSnapshot(string text, DateTimeOffset expires,
        BrowserTextContextPayload? browser)
    {
        var now = _clock.GetUtcNow();
        return new ContextSnapshot(Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 0, browser is null ? null : "browser", now, browser?.Title),
            ContextStatus.Available, new TextSelection(text), now, expires,
            browser is null ? "pack-worker" : "powertrove.provider.browser.explicit", browser?.Url)
        {
            BrowserPage = browser?.Tab is { } tab
                ? new BrowserPageInfo(browser.Url, browser.Title, new Uri(browser.Url).Host,
                    text != browser.Url, text.Length, false, 0, 0, 0, 0, 0, null, 0)
                { Tab = tab } : null
        };
    }

    private ContextSnapshot Snapshot(FileSelectionPayload selection, DateTimeOffset expires)
    {
        var files = selection.Files.Select(f =>
        {
            string path = StagingOutputFileSystem.Canonical(f.Path);
            StagingOutputFileSystem.RejectReparseAncestors(path);
            if (f.IsDirectory ? !Directory.Exists(path) : !File.Exists(path)) throw new InvalidDataException();
            return new SelectedFile(path, Path.GetFileName(path), Path.GetExtension(path).ToLowerInvariant(), null,
                f.IsDirectory ? null : new FileInfo(path).Length, true, true, null, f.IsDirectory);
        }).ToArray();
        var now = _clock.GetUtcNow();
        return new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 0, null, now), ContextStatus.Available,
            new FileSelection(files), now, expires, "pack-worker", null);
    }
    private ContextSnapshot Snapshot(TableSelectionPayload table, DateTimeOffset expires)
    {
        var now = _clock.GetUtcNow();
        return new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 0, null, now), ContextStatus.Available,
            table.ToTableData(), now, expires, "pack-worker", null);
    }
    private sealed class SelectionVerifier(StagingOutputFileSystem files, IFileTypeVerifier verifier) : IFileTypeVerifier
    {
        public async ValueTask<FileTypeVerification> VerifyAsync(string path, FileInputFilter filter, CancellationToken token)
        {
            using (files.OpenRead(path)) { } // Check scope before the trusted signature verifier touches the path.
            return await verifier.VerifyAsync(path, filter, token).ConfigureAwait(false);
        }
    }
    private sealed class InlineProgress(Action<ActionProgress> report) : IProgress<ActionProgress>
    { public void Report(ActionProgress value) => report(value); }
}
