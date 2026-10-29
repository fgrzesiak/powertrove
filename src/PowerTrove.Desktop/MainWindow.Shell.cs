using System;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;

namespace PowerTrove.Desktop;

/// <summary>
/// The main window is the only PowerTrove window. Besides the palette it hosts one view at a time
/// (settings, action options, a visual editor, the page organizer). Switching replaces the content at once,
/// without animation: the window takes the new size around its current center and only shifts as far as it
/// must to stay inside the work area.
/// </summary>
public partial class MainWindow
{
    private HostedView? _hostedView;
    // A view that asked to close stays on screen until the next step (palette, other view or hiding).
    private HostedView? _endingView;
    private RectInt32? _paletteBounds;
    private Button? _backButton;
    private bool _shellSizeHooked;

    /// <summary>The view the window currently shows instead of the palette, if any.</summary>
    internal HostedView? HostedView => _hostedView;

    private bool IsMinimized => AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    /// <summary>
    /// Shows <paramref name="view"/> in this window in place of whatever is on screen. Unless
    /// <paramref name="canReturnToPalette"/> is false, a view that replaced the palette can go back to it.
    /// </summary>
    internal void ShowView(HostedView view, nint sourceWindow = 0, bool canReturnToPalette = true)
    {
        if (ReferenceEquals(_hostedView, view)) { BringHostedViewToFront(); return; }
        bool onScreen = AppWindow.IsVisible && !IsMinimized;
        var previous = _hostedView ?? _endingView;
        if (_hostedView is { } current) EndView(current);
        if (_paletteShown) LeavePalette();
        view.CanReturnToPalette = canReturnToPalette && onScreen && (previous is null || previous.CanReturnToPalette);
        if (!_shellSizeHooked)
        {
            _shellSizeHooked = true;
            ShellRoot.SizeChanged += (_, _) => { if (_hostedView is not null) UpdateBackRegion(); };
        }
        _endingView = null;
        if (!onScreen)
        {
            if (AppWindow.IsVisible) AppWindow.Hide();
            ResetToPalette();
        }
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } maximized)
            maximized.Restore();
        _hostedView = view;
        view.CloseRequested += View_CloseRequested;
        var frame = CreateFrame(view);
        var placed = VisualEditorPlacement.Center(view.PreferredSize,
            sourceWindow != 0 ? sourceWindow : WinRT.Interop.WindowNative.GetWindowHandle(this));
        // A shown window keeps its center; a newly opened one is centered on the monitor.
        var target = onScreen ? AroundCenter(placed.Width, placed.Height) : placed;

        var cover = onScreen ? CaptureCover(target) : null;
        HostedViewHost.Children.Clear();
        HostedViewHost.Children.Add(frame);
        PaletteRoot.Visibility = Visibility.Collapsed;
        HostedViewHost.Visibility = Visibility.Visible;
        ApplyChrome(view);
        AppWindow.MoveAndResize(target);
        if (onScreen) RevealWhenRendered(cover, cloaked: false);
        else ShowWhenRendered();
        Activate();
        ForegroundActivator.BringToFront(this);
        FocusView(view);
    }

    /// <summary>Brings a view back that must not be replaced by the palette; false when none is shown.</summary>
    internal bool TryResurfaceHostedView()
    {
        if (_hostedView is not { KeepsWindow: true }) return false;
        BringHostedViewToFront();
        return true;
    }

    internal void BringHostedViewToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        ForegroundActivator.BringToFront(this);
    }

    /// <summary>Replaces a shown view with the palette, keeping the window's center.</summary>
    private void SwitchToPalette()
    {
        if (_hostedView is { } current) EndView(current);
        _endingView = null;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } presenter)
            presenter.Restore();
        // A palette placed for another monitor (a new invocation there) goes there instead.
        var target = _paletteBounds is not { } palette ? Bounds()
            : Contains(WorkArea(), Center(palette)) ? AroundCenter(palette.Width, palette.Height) : palette;
        var cover = CaptureCover(target);
        HostedViewHost.Children.Clear();
        HostedViewHost.Visibility = Visibility.Collapsed;
        PaletteRoot.Visibility = Visibility.Visible;
        ApplyChrome(null);
        AppWindow.MoveAndResize(target);
        RevealWhenRendered(cover, cloaked: false);
    }

    // ---- Showing only fully rendered content ----
    // WinUI lays out and renders a switched or resized window a few frames late. Until then the window would show
    // the old layout stretched, or black where it grew. A still of the screen covers a switch on screen; a window
    // that appears from hidden stays cloaked. Both end once the content has rendered at the window's size.

    private RevealAfterRender? _reveal;

    /// <summary>A still covering the window's current and next bounds (with their shadows), taken before a switch.</summary>
    private FreezeFrame? CaptureCover(RectInt32 target)
    {
        _reveal?.Dispose();
        int margin = (int)Math.Round(32 * (ShellRoot.XamlRoot?.RasterizationScale ?? 1));
        var area = Union(Bounds(), target);
        return FreezeFrame.Show(new RectInt32(area.X - margin, area.Y - margin, area.Width + 2 * margin, area.Height + 2 * margin),
            WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    /// <summary>Shows a hidden window cloaked and uncloaks it once it has rendered.</summary>
    private void ShowWhenRendered()
    {
        _reveal?.Dispose();
        if (AppWindow.IsVisible) return;
        SetCloak(true);
        AppWindow.Show(activateWindow: true);
        RevealWhenRendered(null, cloaked: true);
    }

    private void RevealWhenRendered(FreezeFrame? cover, bool cloaked)
    {
        _reveal?.Dispose();
        ShellRoot.UpdateLayout();
        _reveal = new RevealAfterRender(this, cover, cloaked);
    }

    private void SetCloak(bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), DwmwaCloak, ref value, sizeof(int));
    }

    /// <summary>
    /// Waits until the content has the window's client size for a few rendered frames (at least a short time, at most
    /// a fallback), lets DWM present that frame, then removes the still or uncloaks the window.
    /// </summary>
    private sealed class RevealAfterRender : IDisposable
    {
        private static readonly TimeSpan Minimum = TimeSpan.FromMilliseconds(40), Maximum = TimeSpan.FromMilliseconds(400);
        private readonly MainWindow _owner;
        private readonly FreezeFrame? _cover;
        private readonly bool _cloaked;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private int _stable;
        private bool _done;

        public RevealAfterRender(MainWindow owner, FreezeFrame? cover, bool cloaked)
        {
            _owner = owner; _cover = cover; _cloaked = cloaked;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        }

        private void OnRendering(object? sender, object e)
        {
            var root = _owner.ShellRoot;
            double scale = root.XamlRoot?.RasterizationScale ?? 1;
            bool sized = GetClientRect(WinRT.Interop.WindowNative.GetWindowHandle(_owner), out var client)
                && Math.Abs(root.ActualWidth - (client.Right - client.Left) / scale) < 1.5
                && Math.Abs(root.ActualHeight - (client.Bottom - client.Top) / scale) < 1.5;
            _stable = sized ? _stable + 1 : 0;
            if ((_stable < 3 || _clock.Elapsed < Minimum) && _clock.Elapsed < Maximum) return;
            DwmFlush();
            Dispose();
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            _cover?.Dispose();
            if (_cloaked) _owner.SetCloak(false);
            if (ReferenceEquals(_owner._reveal, this)) _owner._reveal = null;
        }
    }

    private static RectInt32 Union(RectInt32 a, RectInt32 b)
    {
        int left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.X + a.Width, b.X + b.Width), bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new RectInt32(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Lets the user pick a color anywhere on the desktop, including PowerTrove's own preview, then brings the
    /// window back to the front.
    /// </summary>
    internal static async System.Threading.Tasks.Task<PowerTrove.Platform.Windows.Displays.ScreenColor?> PickScreenColorAsync(Window window)
    {
        try { return await new PowerTrove.Platform.Windows.Displays.ScreenColorPicker().PickAsync(); }
        finally
        {
            window.Activate();
            ForegroundActivator.BringToFront(window);
        }
    }

    /// <summary>Esc closes the window from anywhere in a view; the palette handles its own Esc (cancel, done, hide).</summary>
    private void Shell_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        // An open flyout, dropdown or tooltip closes first; the next Esc closes the window.
        if (ShellRoot.XamlRoot is { } root && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root)
            .Any(popup => popup.Child is not ToolTip)) return;
        if (_hostedView is { } view)
        {
            e.Handled = true;
            view.Close();
        }
        else if (_paletteShown && !_dialogOpen && !ViewModel.IsExecuting && !ViewModel.HasExecutionResult
            && !IsWithin(FocusManager.GetFocusedElement(ShellRoot.XamlRoot) as DependencyObject, PaletteRoot))
        {
            // Focus outside the palette (or none): its own handler does not see the key.
            e.Handled = true;
            (App.Current as App)?.DismissPalette();
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    private const int DwmwaCloak = 13;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    /// <summary>Hides the window; it reopens as the palette.</summary>
    private void HideShell()
    {
        _reveal?.Dispose();
        AppWindow.Hide();
        ResetToPalette();
    }

    /// <summary>While hidden: drop views and return to the palette's chrome, layout and placement.</summary>
    private void ResetToPalette()
    {
        if (_hostedView is { } current) EndView(current);
        _endingView = null;
        HostedViewHost.Children.Clear();
        HostedViewHost.Visibility = Visibility.Collapsed;
        PaletteRoot.Visibility = Visibility.Visible;
        ApplyChrome(null);
        if (_paletteBounds is { } bounds) AppWindow.MoveAndResize(bounds);
    }

    private RectInt32 Bounds() => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    private RectInt32 WorkArea() => DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

    private static bool Contains(RectInt32 rect, PointInt32 point) =>
        point.X >= rect.X && point.X < rect.X + rect.Width && point.Y >= rect.Y && point.Y < rect.Y + rect.Height;

    private static PointInt32 Center(RectInt32 rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    /// <summary>
    /// A rectangle of the given size around the window's current center, shifted (and if needed shrunk)
    /// only as far as it takes to stay inside the work area.
    /// </summary>
    private RectInt32 AroundCenter(int width, int height)
    {
        var area = WorkArea();
        var center = Center(Bounds());
        width = Math.Min(width, area.Width);
        height = Math.Min(height, area.Height);
        int x = Math.Clamp(center.X - width / 2, area.X, area.X + area.Width - width);
        int y = Math.Clamp(center.Y - height / 2, area.Y, area.Y + area.Height - height);
        return new RectInt32(x, y, width, height);
    }

    private void View_CloseRequested(object? sender, EventArgs e)
    {
        if (sender is not HostedView view || !ReferenceEquals(view, _hostedView)) return;
        DetachView(view);
        // Whoever waited for the view (an action flow) continues first; otherwise go back or close.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!ReferenceEquals(_endingView, view) || _hostedView is not null || _paletteShown) return;
            if (view.ReturnToPalette) ShowPalette();
            else (App.Current as App)?.DismissPalette();
        });
    }

    /// <summary>Ends a view but leaves it on screen until the next step replaces it.</summary>
    private void DetachView(HostedView view)
    {
        if (!ReferenceEquals(view, _hostedView)) return;
        EndView(view);
        _endingView = view;
    }

    private void EndView(HostedView view)
    {
        view.CloseRequested -= View_CloseRequested;
        if (ReferenceEquals(_hostedView, view)) _hostedView = null;
        view.NotifyClosed();
    }

    /// <summary>Palette: borderless, always on top, out of Alt+Tab. View: a normal resizable window.</summary>
    private void ApplyChrome(HostedView? view)
    {
        bool palette = view is null;
        AppWindow.IsShownInSwitchers = !palette;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (palette && presenter.State != OverlappedPresenterState.Restored) presenter.Restore();
            presenter.IsResizable = !palette;
            presenter.IsMaximizable = !palette;
            presenter.IsMinimizable = !palette;
            presenter.IsAlwaysOnTop = palette;
            presenter.SetBorderAndTitleBar(true, !palette);
        }
        Title = view?.Title ?? UiText.MainWindowTitle;
        SetTitleBar(view is null ? DragRegion : view.TitleBar ?? TitleStripOf(view));
        if (view is null) _backButton = null;
        UpdateBackRegion();
    }

    /// <summary>The back arrow sits in the drag region; let clicks through to it.</summary>
    private void UpdateBackRegion()
    {
        var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        if (_backButton is not { } button || _hostedView is null || button.XamlRoot is null || button.ActualWidth <= 0)
        {
            source.ClearRegionRects(NonClientRegionKind.Passthrough);
            return;
        }
        double scale = button.XamlRoot.RasterizationScale;
        var bounds = button.TransformToVisual(null).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
        source.SetRegionRects(NonClientRegionKind.Passthrough, [new RectInt32(
            (int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale))]);
    }

    /// <summary>
    /// Frame around a view: the view itself, a title strip when it has no drag region of its own, and a
    /// back arrow in the title bar when the view can go one step back.
    /// </summary>
    private FrameworkElement CreateFrame(HostedView view)
    {
        var frame = new Grid { Tag = view };
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (view.TitleBar is null)
        {
            var strip = new Grid { Height = 40, Padding = new Thickness(view.CanGoBack ? 52 : 16, 0, 140, 0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new Image { Source = AppIconImage.Create(32), Width = 16, Height = 16 });
            label.Children.Add(new TextBlock { Text = view.Title, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CaptionTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
            strip.Children.Add(label);
            frame.Children.Add(strip);
        }
        if (view.Parent is Panel oldParent) oldParent.Children.Remove(view);
        Grid.SetRow(view, 1);
        frame.Children.Add(view);
        _backButton = null;
        if (view.CanGoBack)
        {
            // Views with their own title bar make room for the arrow next to their icon.
            if (view.TitleBar is Grid titleBar)
                titleBar.Padding = new Thickness(titleBar.Padding.Left + 36, titleBar.Padding.Top, titleBar.Padding.Right, titleBar.Padding.Bottom);
            var back = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 },
                Width = 40, Height = 32,
                Margin = new Thickness(6, view.TitleBar is null ? 4 : 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetName(back, UiText.CommonBack);
            // No tooltip: it would stay open after the arrow replaced the view.
            back.Click += (_, _) => { if (ReferenceEquals(_hostedView, view)) view.GoBack(); };
            back.SizeChanged += (_, _) => { if (ReferenceEquals(_backButton, back)) UpdateBackRegion(); };
            Grid.SetRowSpan(back, 2);
            frame.Children.Add(back);
            _backButton = back;
        }
        return frame;
    }

    private FrameworkElement? FrameOf(HostedView view) =>
        HostedViewHost.Children.OfType<FrameworkElement>().FirstOrDefault(f => ReferenceEquals(f.Tag, view));

    private UIElement TitleStripOf(HostedView view) =>
        FrameOf(view) is Grid frame && frame.Children.Count > 1 ? frame.Children[0] : DragRegion;

    private void FocusView(HostedView view)
    {
        void Focus()
        {
            if (!ReferenceEquals(_hostedView, view)) return;
            if ((view.InitialFocus ?? FocusManager.FindFirstFocusableElement(view)) is Control control)
                control.Focus(FocusState.Programmatic);
        }
        if (view.IsLoaded) Focus();
        else view.Loaded += (_, _) => Focus();
    }
}
