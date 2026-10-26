using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using ContextBar.Application.Services;

namespace ContextBar.Platform.Windows;

public sealed class ClipboardWriter : IClipboardWriter
{
    public Task WriteTextAsync(string text)
    {
        if (text == null) return Task.CompletedTask;

        var dataPackage = new DataPackage();
        dataPackage.SetText(text);

        // This must run on a thread with a CoreDispatcher (UI thread in WinUI).
        // The PaletteViewModel execution generally posts back to the UI thread via data binding,
        // or the host handles dispatcher transitions. We directly call the API here.
        Clipboard.SetContent(dataPackage);
        Clipboard.Flush();

        return Task.CompletedTask;
    }
}
