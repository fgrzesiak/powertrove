using PowerTrove.Core;

namespace PowerTrove.Application.ViewModels;

/// <summary>One UI refresh for a burst of pack changes; never reopens the palette or captures another window.</summary>
public sealed class PackRegistryBinding : IDisposable
{
    private readonly IActionRegistryChanges _changes;
    private readonly PaletteViewModel _palette;
    private readonly Func<Action, bool> _dispatch;
    private int _queued, _closed;
    public PackRegistryBinding(IActionRegistryChanges changes, PaletteViewModel palette, Func<Action, bool> dispatch)
    {
        _changes = changes; _palette = palette; _dispatch = dispatch;
        _changes.Changed += Changed;
    }
    private void Changed(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _closed) != 0 || Interlocked.Exchange(ref _queued, 1) != 0) return;
        if (!_dispatch(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            if (Volatile.Read(ref _closed) == 0) _ = _palette.RefreshActionsAsync();
        })) Interlocked.Exchange(ref _queued, 0);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) _changes.Changed -= Changed;
    }
}
