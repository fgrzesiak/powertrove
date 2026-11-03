using System.Collections.Concurrent;
using PowerTrove.Abstractions;
using PowerTrove.Core.Extensibility.PackProtocol;
using PowerTrove.Core.Browser;

namespace PowerTrove.PackHost;

/// <summary>Worker can ask for browser data only for the tab approved in its active request.</summary>
internal sealed class WorkerBrowserPageProxy : IBrowserPageService
{
    private readonly PackMessageWriter _writer;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<BrowserServiceResultMessage>> _pending;
    private readonly ConcurrentDictionary<Guid, WorkerBrowserBinaryPending> _binaryPending;
    private readonly Guid _requestId;
    private readonly BrowserTabHandle _approvedTab;

    public WorkerBrowserPageProxy(PackMessageWriter writer,
        ConcurrentDictionary<Guid, TaskCompletionSource<BrowserServiceResultMessage>> pending,
        ConcurrentDictionary<Guid, WorkerBrowserBinaryPending> binaryPending,
        Guid requestId, BrowserTabHandle approvedTab)
    { _writer = writer; _pending = pending; _binaryPending = binaryPending;
        _requestId = requestId; _approvedTab = approvedTab; }

    public async Task<BrowserPageContent?> FetchAsync(BrowserPageInfo page,
        BrowserPageOperation operation, CancellationToken cancellationToken)
    {
        if (page?.Tab is not { } tab || tab != _approvedTab) return null;
        var id = Guid.NewGuid();
        if (operation is BrowserPageOperation.CaptureVisiblePng or BrowserPageOperation.CaptureFullPng
            or BrowserPageOperation.CaptureMhtml)
        {
            string kind = operation == BrowserPageOperation.CaptureMhtml ? "mhtml" : "image";
            using var pending = new WorkerBrowserBinaryPending(id, kind, operation);
            if (!_binaryPending.TryAdd(id, pending)) return null;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                await _writer.WriteAsync(new BrowserServiceRequestMessage(_requestId, id, operation),
                    deadline.Token).ConfigureAwait(false);
                return await pending.Result.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
            finally { _binaryPending.TryRemove(id, out _); }
        }
        var completion = new TaskCompletionSource<BrowserServiceResultMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) return null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(9));
            await _writer.WriteAsync(new BrowserServiceRequestMessage(_requestId,id,operation),
                deadline.Token).ConfigureAwait(false);
            var result = await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            return result.RequestId == _requestId && result.OperationId == id &&
                result.Content?.Operation == operation ? result.Content : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        finally { _pending.TryRemove(id, out _); }
    }
}


/// <summary>One isolated, ordered operation. Invalid/partial transfers never become files.</summary>
internal sealed class WorkerBrowserBinaryPending : IDisposable
{
    private readonly Guid _id;
    private readonly string _kind;
    private readonly BrowserPageOperation _operation;
    private readonly BrowserChunkBuffer _buffer;
    private readonly object _sync = new();
    private readonly TaskCompletionSource<BrowserPageContent?> _completed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorkerBrowserBinaryPending(Guid id, string kind, BrowserPageOperation operation)
    {
        _id = id; _kind = kind; _operation = operation;
        _buffer = new BrowserChunkBuffer(id, kind);
    }
    public Task<BrowserPageContent?> Result => _completed.Task;
    public void Append(BrowserServiceBinaryChunkMessage message)
    {
        lock (_sync)
        {
            if (_completed.Task.IsCompleted) return;
            try
            {
                if (message.OperationId != _id || message.Kind != _kind)
                    throw new InvalidDataException("Unexpected capture transfer.");
                _buffer.Append(new BrowserWireProtocol.Chunk(message.OperationId, message.Sequence,
                    message.Last, message.Kind, message.Base64));
                if (message.Last) _completed.TrySetResult(
                    new BrowserPageContent(_operation, Binary: _buffer.Complete()));
            }
            catch (Exception ex) when (ex is InvalidDataException or ObjectDisposedException)
            { _completed.TrySetResult(null); }
        }
    }
    public void Fail() => _completed.TrySetResult(null);
    public void Dispose()
    {
        lock (_sync)
        {
            _completed.TrySetResult(null);
            _buffer.Dispose();
        }
    }
}
