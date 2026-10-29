using System.Runtime.InteropServices.WindowsRuntime;
using FileKit.Imaging.Raster;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace PowerTrove.Desktop;

/// <summary>Ephemeral visual layout. Only typed geometry is returned; the registered pack writes the output.</summary>
public sealed class ImageCanvasWindow : HostedView, IVisualActionEditor
{
    private readonly ContextSnapshot snapshot;
    private readonly DictionaryParameters initial;
    private readonly string origin;
    private readonly Func<ActionParameters, CancellationToken, Task<ActionPreview?>> validate;
    private readonly Func<bool> stillCurrent;
    private readonly CancellationTokenSource closing = new();
    private readonly TaskCompletionSource<DictionaryParameters?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Canvas stage = new() { MinHeight = 220 };
    private readonly Canvas paper = new();
    private readonly Canvas viewport = new();
    private readonly Image checker = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Image image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Grid layer = new() { Background = new SolidColorBrush(Colors.Transparent), RenderTransformOrigin = new(.5, .5) };
    private readonly Border outline = new() { BorderThickness = new(1), IsHitTestVisible = false, RenderTransformOrigin = new(.5, .5) };
    private readonly StackPanel toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel controls = new() { Spacing = 10 };
    private readonly ContentControl toolbarHost = new() { IsEnabled = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl controlsHost = new() { IsEnabled = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<string, NumberBox> numbers = new();
    private readonly CheckBox aspect = new() { IsChecked = true };
    private readonly CheckBox canvasMode = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button apply = new() { IsEnabled = false };
    private readonly Button undo = new(), redo = new();
    private readonly List<(Button Button, int X, int Y, bool Rotation)> grips = new();
    private ImageCanvasEditorState? state;
    private ImageCanvasLayout? dragStart;
    private UIElement? dragOwner;
    private uint dragPointerId;
    private Point pointerStart;
    private double scale = 1, dragScale = 1, paperLeft, paperTop;
    private bool sync, closed, saving, viewportReady;
    private DictionaryParameters? result;
    HostedView IVisualActionEditor.HostView => this;
    // The back arrow leads to the action's options, like the "Options" button.
    public override bool CanGoBack => true;
    public override void GoBack() { ReturnToOptions = true; Close(); }
    public bool ReturnToOptions { get; private set; }
    public DictionaryParameters DraftParameters => state is null ? initial : ImageEditorInput.Parameters(state.Layout);
    private static string T(string key) => UiText.ImageEditor(key);

    public ImageCanvasWindow(ContextSnapshot snapshot, DictionaryParameters initial, string origin,
        Func<ActionParameters, CancellationToken, Task<ActionPreview?>> validate, Func<bool> stillCurrent)
    {
        this.snapshot = snapshot; this.initial = initial; this.origin = origin; this.validate = validate; this.stillCurrent = stillCurrent;
        Title = T("Title");
        var root = new Grid { Background = Brush("LayerFillColorDefaultBrush", Colors.Transparent) };
        root.RowDefinitions.Add(new() { Height = new(48) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = new Grid { Padding = new(20, 0, 140, 0) };
        title.Children.Add(new TextBlock { Text = T("Title"), VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(title); TitleBar = title;

        undo.Content = T("Undo"); undo.Click += (_, _) => { state?.Undo(); Refresh(); };
        redo.Content = T("Redo"); redo.Click += (_, _) => { state?.Redo(); Refresh(); };
        toolbar.Children.Add(undo); toolbar.Children.Add(redo);
        AddButton(toolbar, "RotateLeft", () => state!.Rotate(-90));
        AddButton(toolbar, "RotateRight", () => state!.Rotate(90));
        AddButton(toolbar, "FitImage", () => state!.FitImage());
        AddButton(toolbar, "FitCanvas", () => state!.FitCanvas());
        AddButton(toolbar, "FitView", () => { FitView(); return true; });
        AddButton(toolbar, "Reset", () => state!.Reset());
        toolbarHost.Content = toolbar;
        var toolScroll = new ScrollViewer { Content = toolbarHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(16, 0, 16, 8) };
        Grid.SetRow(toolScroll, 1); root.Children.Add(toolScroll);

        var body = new Grid { Margin = new(16, 0, 16, 0), ColumnSpacing = 16, RowSpacing = 12 };
        body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new() { Width = new(280) });
        body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = new(0) });
        stage.Background = Brush("ControlFillColorSecondaryBrush", Colors.Gray);
        AutomationProperties.SetName(stage, T("Preview"));
        outline.BorderBrush = Brush("SystemControlHighlightAccentBrush", Colors.DodgerBlue);
        layer.Children.Add(image); viewport.Children.Add(layer); paper.Children.Add(checker); paper.Children.Add(viewport);
        stage.Children.Add(paper); stage.Children.Add(outline);
        WireGesture(layer, 0, 0);
        foreach (var (x, y, key) in new[] { (-1,-1,"TopLeft"), (0,-1,"Top"), (1,-1,"TopRight"), (-1,0,"Left"), (1,0,"Right"), (-1,1,"BottomLeft"), (0,1,"Bottom"), (1,1,"BottomRight") })
        {
            var grip = Grip(key); grips.Add((grip, x, y, false)); stage.Children.Add(grip); WireGesture(grip, x, y);
        }
        var rotation = Grip("Rotate");
        rotation.Content = new FontIcon { Glyph = "\uE7AD", FontSize = 12, Foreground = new SolidColorBrush(Colors.DodgerBlue) };
        rotation.Width = rotation.Height = 24; grips.Add((rotation, 0, -1, true)); stage.Children.Add(rotation); WireGesture(rotation, 0, 0, true);
        body.Children.Add(stage);

        controls.Children.Add(new TextBlock { Text = T("Help"), TextWrapping = TextWrapping.Wrap });
        controls.Children.Add(new TextBlock { Text = T("Modifiers"), TextWrapping = TextWrapping.Wrap });
        canvasMode.Content = T("CanvasMode"); controls.Children.Add(canvasMode);
        canvasMode.Checked += (_, _) => Refresh(); canvasMode.Unchecked += (_, _) => Refresh();
        AddNumberRow("CanvasWidth", "CanvasHeight");
        AddNumberRow("ImageWidth", "ImageHeight");
        aspect.Content = T("LockAspect"); controls.Children.Add(aspect);
        aspect.Checked += (_, _) => { if (state is not null) state.LockAspect = true; };
        aspect.Unchecked += (_, _) => { if (state is not null) state.LockAspect = false; };
        AddNumberRow("OffsetX", "OffsetY", -20000, 20000);
        AddNumberRow("Angle", null, -180, 180);
        controls.Children.Add(new TextBlock { Text = T("Align") });
        var align = new Grid { ColumnSpacing = 4, RowSpacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 0; i < 3; i++) { align.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); align.RowDefinitions.Add(new() { Height = GridLength.Auto }); }
        string[] directions = ["TopLeft", "Top", "TopRight", "Left", "Center", "Right", "BottomLeft", "Bottom", "BottomRight"];
        string[] arrows = ["\u2196", "\u2191", "\u2197", "\u2190", "\u2022", "\u2192", "\u2199", "\u2193", "\u2198"];
        for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
        {
            int h = x - 1, v = y - 1;
            var button = new Button { Width = 42, Height = 32, Content = new FontIcon { Glyph = arrows[y * 3 + x], FontFamily = new("Segoe UI"), FontSize = 16 } };
            AutomationProperties.SetName(button, UiText.Format(T("AlignAt"), T(directions[y * 3 + x])));
            ToolTipService.SetToolTip(button, UiText.Format(T("AlignAt"), T(directions[y * 3 + x])));
            button.Click += (_, _) => Edit(() => state!.Align(h, v)); Grid.SetColumn(button, x); Grid.SetRow(button, y); align.Children.Add(button);
        }
        controls.Children.Add(align); controls.Children.Add(summary);
        controls.Children.Add(new TextBlock { Text = T("SaveHint"), TextWrapping = TextWrapping.Wrap });
        controlsHost.Content = controls;
        var sidebar = new ScrollViewer { Content = controlsHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(sidebar, 1); body.Children.Add(sidebar);
        body.SizeChanged += (_, _) =>
        {
            bool narrow = body.ActualWidth < 820;
            body.ColumnDefinitions[1].Width = new(narrow ? 0 : 280);
            body.RowDefinitions[1].Height = narrow ? GridLength.Auto : new(0);
            Grid.SetRow(sidebar, narrow ? 1 : 0); Grid.SetColumn(sidebar, narrow ? 0 : 1); Grid.SetColumnSpan(sidebar, narrow ? 2 : 1);
            Grid.SetColumnSpan(stage, narrow ? 2 : 1);
            stage.MinHeight = narrow ? 140 : 220;
            sidebar.MaxHeight = narrow ? Math.Min(250, Math.Max(120, body.ActualHeight * .4)) : double.PositiveInfinity;
        };
        // Layout changes (for example a wrapped status message) must not silently change the zoom.
        stage.SizeChanged += (_, _) => Refresh();
        Grid.SetRow(body, 2); root.Children.Add(body);

        var footer = new Grid { Padding = new(16, 12, 16, 16), ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        status.Text = T("Loading"); footer.Children.Add(status);
        var back = new Button { Content = T("Back") }; back.Click += (_, _) => { ReturnToOptions = true; Close(); };
        apply.Content = T("Save"); apply.Click += async (_, _) => await ApplyAsync();
        var cancel = CreateCancelButton(); Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        Grid.SetColumn(back, 2); footer.Children.Add(back); Grid.SetColumn(apply, 3); footer.Children.Add(apply);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        toolbarHost.IsEnabled = controlsHost.IsEnabled = false;
        Content = root;
        root.KeyDown += (_, args) => { if (args.Key == global::Windows.System.VirtualKey.Escape) { args.Handled = true; Close(); } };
        PreferredSize = new(1180, 850);
        Closed += (_, _) => { if (!closed) { closed = true; closing.Cancel(); image.Source = null; completion.TrySetResult(result); } };
    }

    public Task<DictionaryParameters?> ShowSelectionAsync() { _ = LoadAsync(); return completion.Task; }

    private async Task LoadAsync()
    {
        try
        {
            if (!ImageEditorInput.CanEdit(snapshot) || !stillCurrent()) { status.Text = UiText.PackSelectAgain; return; }
            // Decode and thumbnail generation stay in the installed pack/worker. The UI receives a bounded PNG only.
            var preview = await validate(new EmptyParameters(), closing.Token);
            if (closed) return;
            if (preview is not { IsBlocked: false, ImagePreview: { } frame } || !frame.TryDecodePng(out var bytes))
            { status.Text = preview?.PreviewText ?? T("LoadFailed"); return; }
            state = new(frame.SourceWidth, frame.SourceHeight, ImageEditorInput.Seed(frame.SourceWidth, frame.SourceHeight, origin, initial.Values));
            using var memory = new MemoryStream(bytes, false);
            using var stream = memory.AsRandomAccessStream();
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream).AsTask(closing.Token);
            if (closed) return;
            image.Source = bitmap; checker.Source = Checker(); toolbarHost.IsEnabled = controlsHost.IsEnabled = true; Refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!closed) status.Text = T("LoadFailed"); }
    }

    private async Task ApplyAsync()
    {
        if (state is null || saving || !stillCurrent() || snapshot.IsStale) { apply.IsEnabled = false; status.Text = UiText.PackSelectAgain; return; }
        saving = true; apply.IsEnabled = toolbarHost.IsEnabled = controlsHost.IsEnabled = false; status.Text = UiText.OptionsChecking;
        try
        {
            var chosen = ImageEditorInput.Parameters(state.Layout);
            var preview = await validate(chosen, closing.Token);
            if (closed) return;
            if (preview is { IsBlocked: false, Confirmation: null } && stillCurrent() && !snapshot.IsStale)
            { result = chosen; Close(); return; }
            status.Text = preview?.PreviewText ?? UiText.PackSelectAgain;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!closed) status.Text = T("LoadFailed"); }
        finally { if (!closed) { saving = false; toolbarHost.IsEnabled = controlsHost.IsEnabled = true; apply.IsEnabled = stillCurrent() && !snapshot.IsStale; } }
    }

    private void Refresh()
    {
        if (closed || state is null) return;
        var l = state.Layout;
        if (!viewportReady) UpdateViewport();
        double w = l.CanvasWidth * scale, h = l.CanvasHeight * scale;
        paper.Width = checker.Width = viewport.Width = w; paper.Height = checker.Height = viewport.Height = h;
        Canvas.SetLeft(paper, paperLeft); Canvas.SetTop(paper, paperTop);
        viewport.Clip = new RectangleGeometry { Rect = new(0, 0, w, h) };
        stage.Clip = new RectangleGeometry { Rect = new(0, 0, Math.Max(1, stage.ActualWidth), Math.Max(1, stage.ActualHeight)) };
        layer.Width = outline.Width = l.ImageWidth * scale; layer.Height = outline.Height = l.ImageHeight * scale;
        double left = l.CenterX * scale - layer.Width / 2, top = l.CenterY * scale - layer.Height / 2;
        Canvas.SetLeft(layer, left); Canvas.SetTop(layer, top); Canvas.SetLeft(outline, paperLeft + left); Canvas.SetTop(outline, paperTop + top);
        layer.RenderTransform = new RotateTransform { Angle = l.Angle }; outline.RenderTransform = new RotateTransform { Angle = l.Angle };
        bool canvas = canvasMode.IsChecked == true;
        foreach (var (button, x, y, rotation) in grips)
        {
            bool shown = !canvas || !rotation && x >= 0 && y >= 0;
            button.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            double px, py;
            if (canvas) { px = paperLeft + (x + 1) * w / 2; py = paperTop + (y + 1) * h / 2; }
            else
            {
                double dx = x * layer.Width / 2, dy = y * layer.Height / 2 - (rotation ? 30 : 0);
                px = paperLeft + l.CenterX * scale + Math.Cos(l.Radians) * dx - Math.Sin(l.Radians) * dy;
                py = paperTop + l.CenterY * scale + Math.Sin(l.Radians) * dx + Math.Cos(l.Radians) * dy;
            }
            Canvas.SetLeft(button, px - button.Width / 2); Canvas.SetTop(button, py - button.Height / 2);
        }
        sync = true;
        numbers["CanvasWidth"].Value = l.CanvasWidth; numbers["CanvasHeight"].Value = l.CanvasHeight;
        numbers["ImageWidth"].Value = l.ImageWidth; numbers["ImageHeight"].Value = l.ImageHeight;
        numbers["OffsetX"].Value = l.OffsetX; numbers["OffsetY"].Value = l.OffsetY; numbers["Angle"].Value = l.Angle;
        sync = false;
        undo.IsEnabled = state.CanUndo; redo.IsEnabled = state.CanRedo;
        summary.Text = UiText.Format(T("Summary"), l.CanvasWidth, l.CanvasHeight, l.ImageWidth, l.ImageHeight, l.Angle);
        if (!saving) status.Text = !stillCurrent() || snapshot.IsStale ? UiText.PackSelectAgain : l.IsClipped ? T("Clipped") : T("Ready");
        apply.IsEnabled = !saving && stillCurrent() && !snapshot.IsStale;
    }

    private void UpdateViewport()
    {
        if (state is null || stage.ActualWidth <= 0 || stage.ActualHeight <= 0) return;
        var l = state.Layout;
        scale = Math.Max(.001, Math.Min(Math.Max(1, stage.ActualWidth - 100) / l.CanvasWidth,
            Math.Max(1, stage.ActualHeight - 100) / l.CanvasHeight));
        paperLeft = (stage.ActualWidth - l.CanvasWidth * scale) / 2;
        paperTop = (stage.ActualHeight - l.CanvasHeight * scale) / 2;
        viewportReady = true;
    }

    private void FitView() { if (closed || dragStart is not null) return; UpdateViewport(); Refresh(); }

    private void Edit(Func<bool> change)
    { if (state is null || saving) return; bool valid = change(); Refresh(); if (!valid) status.Text = T("Invalid"); }

    private void WireGesture(UIElement element, int x, int y, bool rotation = false)
    {
        // Button handles pointer events internally; grips must also receive those routed events.
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (state is null || saving || dragStart is not null || !args.GetCurrentPoint(stage).Properties.IsLeftButtonPressed) return;
            if (!element.CapturePointer(args.Pointer) &&
                element.PointerCaptures?.Any(pointer => pointer.PointerId == args.Pointer.PointerId) != true) return;
            dragOwner = element; dragPointerId = args.Pointer.PointerId;
            pointerStart = args.GetCurrentPoint(stage).Position; dragStart = state.Layout; dragScale = scale; args.Handled = true;
        }), true);
        element.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            if (state is null || saving || dragStart is not { } start || !ReferenceEquals(dragOwner, element) || args.Pointer.PointerId != dragPointerId) return;
            var point = args.GetCurrentPoint(stage).Position;
            if (rotation)
            {
                double cx = paperLeft + start.CenterX * dragScale, cy = paperTop + start.CenterY * dragScale;
                double delta = (Math.Atan2(point.Y - cy, point.X - cx) - Math.Atan2(pointerStart.Y - cy, pointerStart.X - cx)) * 180 / Math.PI;
                int angle = ImageCanvasEditorState.Normalize(start.Angle + (int)Math.Round(delta));
                state.Set(start with { Angle = ShiftDown ? ImageCanvasEditorState.SnapAngle(angle) : angle }, false);
            }
            else state.Drag(start, x, y, (point.X - pointerStart.X) / dragScale, (point.Y - pointerStart.Y) / dragScale,
                canvasMode.IsChecked == true && element != layer, constrain: ShiftDown);
            Refresh(); args.Handled = true;
        }), true);
        void End(uint pointerId)
        {
            if (state is not null && dragStart is { } start && ReferenceEquals(dragOwner, element) && pointerId == dragPointerId)
            { state.FinishGesture(start); dragStart = null; dragOwner = null; Refresh(); }
        }
        element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) =>
        {
            if (!ReferenceEquals(dragOwner, element) || args.Pointer.PointerId != dragPointerId) return;
            End(args.Pointer.PointerId); element.ReleasePointerCapture(args.Pointer); args.Handled = true;
        }), true);
        element.PointerCaptureLost += (_, args) => End(args.Pointer.PointerId);
        element.KeyDown += (_, args) =>
        {
            if (state is null || saving) return;
            int dx = args.Key == global::Windows.System.VirtualKey.Left ? -1 : args.Key == global::Windows.System.VirtualKey.Right ? 1 : 0;
            int dy = args.Key == global::Windows.System.VirtualKey.Up ? -1 : args.Key == global::Windows.System.VirtualKey.Down ? 1 : 0;
            if (dx == 0 && dy == 0) return;
            var start = state.Layout;
            bool shift = ShiftDown;
            if (rotation) state.Rotate((dx + dy) * (shift ? 15 : 1));
            else { state.Drag(start, x, y, dx * (shift ? 10 : 1), dy * (shift ? 10 : 1),
                canvasMode.IsChecked == true && element != layer, constrain: shift); state.FinishGesture(start); }
            Refresh(); args.Handled = true;
        };
    }

    private static bool ShiftDown => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Shift)
        .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void AddNumberRow(string first, string? second, int minimum = 1, int maximum = 10000)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new());
        foreach (var key in second is null ? new[] { first } : new[] { first, second })
        {
            var number = new NumberBox { Header = T(key), Minimum = minimum, Maximum = maximum, SmallChange = 1, LargeChange = 10,
                Value = Math.Max(0, minimum), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                NumberFormatter = new global::Windows.Globalization.NumberFormatting.DecimalFormatter { IntegerDigits = 1, FractionDigits = 0 } };
            AutomationProperties.SetName(number, T(key)); numbers[key] = number;
            number.ValueChanged += (_, args) =>
            {
                if (sync || state is null || !double.IsFinite(args.NewValue)) return;
                int value = (int)Math.Round(args.NewValue); var l = state.Layout;
                Edit(() => key switch { "CanvasWidth" => state.Set(l with { CanvasWidth = value }), "CanvasHeight" => state.Set(l with { CanvasHeight = value }),
                    "ImageWidth" => state.SetImageWidth(value), "ImageHeight" => state.SetImageHeight(value),
                    "OffsetX" => state.Set(l with { OffsetX = value }), "OffsetY" => state.Set(l with { OffsetY = value }), "Angle" => state.Set(l with { Angle = value }), _ => false });
            };
            Grid.SetColumn(number, key == first ? 0 : 1); row.Children.Add(number);
        }
        controls.Children.Add(row);
    }
    private void AddButton(Panel parent, string key, Func<bool> action)
    { var button = new Button { Content = T(key) }; button.Click += (_, _) => Edit(action); parent.Children.Add(button); }
    private static Button Grip(string key)
    {
        var button = new Button { Width = 16, Height = 16, MinWidth = 16, MinHeight = 16, Padding = new(0), CornerRadius = new(8),
            Background = new SolidColorBrush(Colors.White), BorderBrush = new SolidColorBrush(Colors.DodgerBlue), BorderThickness = new(2) };
        string name = UiText.Format(T("Handle"), T(key)); AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name); return button;
    }
    internal static WriteableBitmap Checker()
    {
        var bitmap = new WriteableBitmap(256, 256); var pixels = new byte[256 * 256 * 4];
        for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++)
        { int i = (y * 256 + x) * 4; byte c = (byte)(((x / 8 + y / 8) % 2 == 0) ? 220 : 185); pixels[i] = pixels[i + 1] = pixels[i + 2] = c; pixels[i + 3] = 255; }
        using var stream = bitmap.PixelBuffer.AsStream(); stream.Write(pixels); bitmap.Invalidate(); return bitmap;
    }
    private static Brush Brush(string key, global::Windows.UI.Color fallback) =>
        Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(fallback);
}
