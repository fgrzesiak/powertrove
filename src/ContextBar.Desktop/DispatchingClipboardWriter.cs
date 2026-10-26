using System;
using System.Threading.Tasks;
using ContextBar.Application.Services;
using Microsoft.UI.Dispatching;

namespace ContextBar.Desktop;

/// <summary>
/// Runs clipboard writes on the palette's UI thread; the WinRT clipboard requires it.
/// Only receipts with explicit ClipboardText reach this writer (see PaletteViewModel).
/// </summary>
internal sealed class DispatchingClipboardWriter : IClipboardWriter
{
    private readonly IClipboardWriter _inner;
    private readonly DispatcherQueue _dispatcher;

    public DispatchingClipboardWriter(IClipboardWriter inner, DispatcherQueue dispatcher)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task WriteTextAsync(string text)
    {
        if (_dispatcher.HasThreadAccess)
            return _inner.WriteTextAsync(text);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await _inner.WriteTextAsync(text);
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        });
        if (!queued)
            completion.SetException(new InvalidOperationException("The clipboard is not available."));
        return completion.Task;
    }
}
