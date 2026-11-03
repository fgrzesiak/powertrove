using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using PowerTrove.Abstractions;
using Microsoft.Win32.SafeHandles;
using PowerTrove.Core.Browser;

namespace PowerTrove.Platform.Windows.Browser;

/// <summary>
/// Current-user, current-session browser companion listener. Only this application's
/// installed BrowserHost binary can connect, verified by kernel-reported client PID.
/// No payloads, URLs or page titles are written to diagnostics.
/// </summary>
public sealed class BrowserSessionServer : IAsyncDisposable
{
    public const int MaximumSessions = 8;
    private readonly string _pipeName;
    private readonly string _expectedHost;
    private readonly int _sessionId;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private int _disposed;
    public int ConnectedSessions => _sessions.Count;

    public BrowserSessionServer(string appDirectory, string? pipeName = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _expectedHost = BrowserNativeHostRegistration.HostExecutable(appDirectory);
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("User SID unavailable.");
        using var process = Process.GetCurrentProcess();
        _sessionId = process.SessionId;
        _pipeName = pipeName ?? $"PowerTrove_Browser_{sid}_{_sessionId}";
    }

    public async Task ListenAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        while (!linked.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // CurrentUserOnly prevents another Windows account from attaching.
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaximumSessions,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    65536, 65536);
                await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
                if (_sessions.Count >= MaximumSessions || !ValidateClient(pipe.SafePipeHandle))
                {
                    pipe.Dispose();
                    continue;
                }
                var id = Guid.NewGuid();
                var session = new Session(id, pipe);
                if (!_sessions.TryAdd(id, session))
                {
                    pipe.Dispose();
                    continue;
                }
                pipe = null; // Session now owns the stream.
                _ = RunSessionAsync(session, linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            {
                try { await Task.Delay(100, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally { pipe?.Dispose(); }
        }
    }

    private bool ValidateClient(SafePipeHandle pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe, out uint pid) || pid == 0 || pid > int.MaxValue)
            return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.HasExited || process.SessionId != _sessionId) return false;
            string actual = Path.GetFullPath(process.MainModule?.FileName ?? "");
            return string.Equals(actual, _expectedHost, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException) { return false; }
    }

    private async Task RunSessionAsync(Session session, CancellationToken serverToken)
    {
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            lifetime.CancelAfter(TimeSpan.FromSeconds(5));
            var hello = await BrowserWireFraming.ReadAsync(session.Pipe, lifetime.Token);
            if (hello is not BrowserWireProtocol.Hello greeting ||
                BrowserWireProtocol.Negotiate(greeting.MinProtocol, greeting.MaxProtocol) is null)
                return;
            session.Browser = greeting.Browser;
            await BrowserWireFraming.WriteAsync(session.Pipe, new BrowserWireProtocol.Ready(
                BrowserWireProtocol.MinimumVersion, BrowserWireProtocol.CurrentVersion,
                ["context.fast"]), lifetime.Token);
            lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
            while (!serverToken.IsCancellationRequested)
            {
                var message = await BrowserWireFraming.ReadAsync(session.Pipe, serverToken);
                if (message is BrowserWireProtocol.Chunk chunk &&
                    session.BinaryPending.TryGetValue(chunk.Id, out var transfer))
                {
                    // Unknown transfers, interleaved IDs and invalid ordering fail closed.
                    transfer.Append(chunk);
                }
                else if (message is BrowserWireProtocol.Response response &&
                    session.BinaryPending.TryRemove(response.Id, out var binary))
                {
                    binary.Complete(response);
                }
                else if (message is BrowserWireProtocol.Response normal &&
                    session.Pending.TryRemove(normal.Id, out var completion))
                    completion.TrySetResult(normal);
                // A late answer to a request that already timed out or was canceled is dropped;
                // ending the session for it would disconnect every slow page.
                else if (message is BrowserWireProtocol.Response or BrowserWireProtocol.Chunk) continue;
                else break; // Never accept unsolicited calls or handshakes.
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException
            or InvalidDataException or InvalidOperationException or ObjectDisposedException) { }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            foreach (var completion in session.Pending.Values)
                completion.TrySetResult(null);
            foreach (var binary in session.BinaryPending.Values) binary.Fail();
            session.Dispose();
        }
    }

    /// <summary>Query live browser windows only on an explicit PowerTrove invocation.</summary>
    public async Task<IReadOnlyList<BrowserPageResult>> QueryAsync(CancellationToken token)
    {
        var sessions = _sessions.Values.Where(s => s.Browser is not null).ToArray();
        var tasks = sessions.Select(s => RequestContextAsync(s, token));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(r => r is not null).Cast<BrowserPageResult>().ToArray();
    }

    private async Task<BrowserPageResult?> RequestContextAsync(Session session, CancellationToken token)
    {
        var id = Guid.NewGuid();
        var completion = new TaskCompletionSource<BrowserWireProtocol.Response?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!session.Pending.TryAdd(id, completion)) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
            await session.WriteLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await BrowserWireFraming.WriteAsync(session.Pipe,
                    new BrowserWireProtocol.Request(id, "getContext", 350), timeout.Token);
            }
            finally { session.WriteLock.Release(); }
            var response = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (response is not { Ok: true, Result: { } result }) return null;
            return BrowserPageResult.Read(session.Browser!, result, session.Id);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException
            or ObjectDisposedException) { return null; }
        finally { session.Pending.TryRemove(id, out _); }
    }

    /// <summary>
    /// On-demand request bound to the original browser session/tab/document. The extension
    /// rechecks navigation before and after extraction; this host never chooses a new tab.
    /// </summary>
    public async Task<JsonElement?> FetchAsync(BrowserTabHandle tab, string operation, CancellationToken token)
    {
        if (tab is null || tab.SessionId == Guid.Empty ||
            operation is not ("extractArticle" or "extractPage" or "extractTables" or "listLinks" or "listImages") ||
            !_sessions.TryGetValue(tab.SessionId, out var session) || session.Browser is null)
            return null;
        var id = Guid.NewGuid();
        var reply = new TaskCompletionSource<BrowserWireProtocol.Response?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!session.Pending.TryAdd(id, reply)) return null;
        bool sent = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(7));
            await session.WriteLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                var handle = new BrowserWireProtocol.TabHandle(tab.SessionId,
                    tab.WindowId, tab.TabId, tab.DocumentId);
                await BrowserWireFraming.WriteAsync(session.Pipe,
                    new BrowserWireProtocol.Request(id, operation, 6000, handle),
                    deadline.Token).ConfigureAwait(false);
                sent = true;
            }
            finally { session.WriteLock.Release(); }
            var response = await reply.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (response is not { Ok: true, Result: { } data } ||
                !data.TryGetProperty("content", out var content)) return null;
            return content.Clone();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException
            or InvalidOperationException or ObjectDisposedException) { return null; }
        finally
        {
            session.Pending.TryRemove(id, out _);
            if (sent && !reply.Task.IsCompleted)
            {
                // Best-effort cancellation; never block the palette to deliver it.
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                try
                {
                    if (await session.WriteLock.WaitAsync(0, cancel.Token).ConfigureAwait(false))
                    {
                        try { await BrowserWireFraming.WriteAsync(session.Pipe,
                            new BrowserWireProtocol.Cancel(id), cancel.Token).ConfigureAwait(false); }
                        finally { session.WriteLock.Release(); }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }

    /// <summary>
    /// On-demand binary operation tied to an already authorized browser tab.
    /// Only bounded, ordered chunks from that exact native session are accepted.
    /// </summary>
    public async Task<byte[]?> FetchBinaryAsync(BrowserTabHandle tab, string operation,
        CancellationToken token)
    {
        if (tab is null || tab.SessionId == Guid.Empty ||
            operation is not ("captureVisible" or "captureFullPage" or "saveMhtml") ||
            !_sessions.TryGetValue(tab.SessionId, out var session) || session.Browser is null)
            return null;
        var id = Guid.NewGuid();
        using var capture = new BinaryCapture(id, operation == "saveMhtml" ? "mhtml" : "image");
        if (!session.BinaryPending.TryAdd(id, capture)) return null;
        bool sent = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            await session.WriteLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                var handle = new BrowserWireProtocol.TabHandle(tab.SessionId,
                    tab.WindowId, tab.TabId, tab.DocumentId);
                await BrowserWireFraming.WriteAsync(session.Pipe,
                    new BrowserWireProtocol.Request(id, operation, 30000, handle),
                    deadline.Token).ConfigureAwait(false);
                sent = true;
            }
            finally { session.WriteLock.Release(); }
            return await capture.Result.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException
            or InvalidDataException or ObjectDisposedException) { return null; }
        finally
        {
            session.BinaryPending.TryRemove(id, out _);
            if (sent && !capture.Result.IsCompleted)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                try
                {
                    if (await session.WriteLock.WaitAsync(0, cancel.Token).ConfigureAwait(false))
                    {
                        try { await BrowserWireFraming.WriteAsync(session.Pipe,
                            new BrowserWireProtocol.Cancel(id), cancel.Token).ConfigureAwait(false); }
                        finally { session.WriteLock.Release(); }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException
                    or ObjectDisposedException) { }
            }
        }
    }

    public void DisconnectSessions()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        // The receive loops remove their own entries, avoiding stale bookkeeping.
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        _lifetime.Dispose();
    }

    private sealed class Session : IDisposable
    {
        public Guid Id { get; }
        public NamedPipeServerStream Pipe { get; }
        public string? Browser { get; set; }
        public SemaphoreSlim WriteLock { get; } = new(1,1);
        public ConcurrentDictionary<Guid, TaskCompletionSource<BrowserWireProtocol.Response?>> Pending { get; } = new();
        public ConcurrentDictionary<Guid, BinaryCapture> BinaryPending { get; } = new();
        public Session(Guid id, NamedPipeServerStream pipe) { Id=id; Pipe=pipe; }
        public void Dispose() { Pipe.Dispose(); WriteLock.Dispose(); }
    }

    private sealed class BinaryCapture : IDisposable
    {
        private readonly BrowserChunkBuffer _buffer;
        private readonly string _kind;
        private readonly TaskCompletionSource<byte[]?> _done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<byte[]?> Result => _done.Task;
        public BinaryCapture(Guid id, string kind)
        {
            _kind = kind;
            _buffer = new BrowserChunkBuffer(id, kind);
        }
        public void Append(BrowserWireProtocol.Chunk chunk)
        {
            if (chunk.Kind != _kind) throw new InvalidDataException("Unexpected binary kind.");
            _buffer.Append(chunk);
        }
        public void Complete(BrowserWireProtocol.Response response)
        {
            if (!response.Ok)
            {
                Fail();
                return;
            }
            if (!_buffer.IsComplete || response.Result is not { ValueKind: JsonValueKind.Object } result ||
                !result.TryGetProperty("kind", out var kind) ||
                kind.GetString() != _kind ||
                !result.TryGetProperty("bytes", out var count) ||
                !count.TryGetInt32(out var length) || length != _buffer.Length)
                throw new InvalidDataException("Incomplete or mismatched capture.");
            byte[] bytes = _buffer.Complete();
            // Do not trust the browser-supplied kind without examining the magic bytes.
            if (_kind == "image" &&
                (bytes.Length < 8 || !bytes.AsSpan(0,8).SequenceEqual(
                    new byte[] { 137,80,78,71,13,10,26,10 })))
                throw new InvalidDataException("Browser capture is not PNG.");
            if (_kind == "mhtml" && (bytes.Length < 14 ||
                !System.Text.Encoding.ASCII.GetString(bytes,0,Math.Min(bytes.Length,4096))
                    .Contains("MIME-Version:", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Browser capture is not MHTML.");
            _done.TrySetResult(bytes);
        }
        public void Fail() => _done.TrySetResult(null);
        public void Dispose() => _buffer.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle, out uint processId);
}
