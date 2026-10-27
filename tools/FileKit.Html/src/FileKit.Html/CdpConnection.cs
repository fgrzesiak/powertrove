using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace FileKit.Html;

/// <summary>Private loopback IPC to one ephemeral browser target. No protocol/content logging.</summary>
internal sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _reader;
    private int _sequence;
    private volatile TaskCompletionSource<bool>? _loaded;
    private volatile bool _navigation;
    public bool AttemptedNavigation => _navigation;
    public async Task ConnectAsync(Uri address, CancellationToken ct)
    { _socket.Options.Proxy = null; await _socket.ConnectAsync(address, ct).ConfigureAwait(false); _reader = ReadAsync(); }
    public Task WaitForLoadAsync(CancellationToken ct)
    { _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously); return _loaded.Task.WaitAsync(ct); }
    public async Task<JsonElement> CommandAsync(string method, object? parameters, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            byte[] message = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await _send.WaitAsync(ct).ConfigureAwait(false);
            try { await _socket.SendAsync(message, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { _send.Release(); }
            return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }
    private async Task ReadAsync()
    {
        try
        {
            byte[] buffer = new byte[16384];
            while (!_stop.IsCancellationRequested)
            {
                using var message = new MemoryStream(); ValueWebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer.AsMemory(), _stop.Token).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close) throw new HtmlPrintException("Browser IPC closed.");
                    if (message.Length + received.Count > 4 * 1024 * 1024) throw new HtmlPrintException("Browser response limit exceeded.");
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length), new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id) && _pending.TryGetValue(id.GetInt32(), out var completion))
                {
                    if (root.TryGetProperty("error", out _)) completion.TrySetException(new HtmlPrintException("The local browser could not complete the print operation."));
                    else completion.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                }
                else if (root.TryGetProperty("method", out var method))
                {
                    if (method.GetString() == "Page.loadEventFired") _loaded?.TrySetResult(true);
                    if (method.GetString() is "Page.frameRequestedNavigation" or "Page.frameScheduledNavigation") _navigation = true;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or JsonException or HtmlPrintException or OperationCanceledException)
        {
            var failure = new HtmlPrintException("The local browser connection ended.");
            foreach (var completion in _pending.Values) completion.TrySetException(failure);
            _loaded?.TrySetException(failure);
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _socket.Abort();
        if (_reader is not null) await _reader.ConfigureAwait(false);
        _socket.Dispose(); _stop.Dispose(); _send.Dispose();
    }
}
