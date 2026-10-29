using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace PowerTrove.Desktop;

/// <summary>
/// A page that the main window shows in place of the palette (settings, visual editors, page organizer).
/// There is only one PowerTrove window: it switches between the palette and a hosted view instead of
/// opening a second window. Views keep their former window names so build outputs stay stable.
/// </summary>
public class HostedView : UserControl
{
    /// <summary>Window title while the view is shown.</summary>
    public string Title { get; protected set; } = string.Empty;

    /// <summary>The view's own drag region; when null, the window adds a plain title strip above it.</summary>
    public UIElement? TitleBar { get; protected set; }

    /// <summary>Window size in logical pixels the view asks for; it is clamped to the work area.</summary>
    public Size PreferredSize { get; protected set; } = new(1000, 760);

    /// <summary>
    /// True when the shortcut should bring this view back instead of replacing it with the palette,
    /// for example while it holds unsaved edits or runs work.
    /// </summary>
    public virtual bool KeepsWindow => true;

    /// <summary>Control that receives focus when the view appears; null focuses the first focusable control.</summary>
    protected internal virtual Control? InitialFocus => null;

    /// <summary>False while closing would interrupt work an update must not cut off.</summary>
    public virtual bool CanCloseForUpdate => true;

    /// <summary>True when the view was left to return to the palette instead of closing the window.</summary>
    public bool ReturnToPalette { get; protected set; }

    /// <summary>Set by the window when the palette this view replaced can be shown again.</summary>
    public bool CanReturnToPalette { get; internal set; }

    /// <summary>Whether the title bar offers a back arrow.</summary>
    public virtual bool CanGoBack => CanReturnToPalette;

    /// <summary>One step back; by default to the palette the view replaced.</summary>
    public virtual void GoBack() => CancelToPalette();

    /// <summary>Leaves the view without a result and shows the palette again.</summary>
    protected void CancelToPalette()
    {
        ReturnToPalette = true;
        Close();
    }

    /// <summary>A footer "Cancel" button that returns to the palette.</summary>
    protected Button CreateCancelButton()
    {
        var button = new Button { Content = UiText.CommonCancel };
        button.Click += (_, _) => CancelToPalette();
        return button;
    }

    /// <summary>Raised when the view asks to be closed; the window decides where to go next.</summary>
    internal event EventHandler? CloseRequested;

    /// <summary>Raised once when the window has removed the view; release resources here.</summary>
    public event EventHandler? Closed;

    private bool _closed;

    internal bool IsClosed => _closed;

    /// <summary>Asks the window to close this view.</summary>
    public void Close()
    {
        if (!_closed) CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void NotifyClosed()
    {
        if (_closed) return;
        _closed = true;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
