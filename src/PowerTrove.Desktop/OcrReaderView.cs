using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Ocr;
using PowerTrove.Application.Localization;
using PowerTrove.Platform.Windows;
using PowerTrove.Platform.Windows.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.System;

namespace PowerTrove.Desktop;

/// <summary>On-device OCR reader. A decoded bitmap is shared by the viewer and its word coordinates.</summary>
public sealed partial class OcrReaderView : HostedView
{
    private readonly ContextSnapshot _snapshot;
    private readonly Func<string?, CancellationToken, Task<string?>> _exportTextFile;
    private readonly WindowsTextRecognizer _recognizer = new();
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _run;
    private readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    // Word marks stay below the selection; the transparent canvas on top takes the pointer.
    private readonly Canvas _marks = new() { IsHitTestVisible = false };
    private readonly Canvas _selection = new() { IsHitTestVisible = false };
    private readonly TextCanvas _overlay = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly ScrollViewer _scroll = new() {
        ZoomMode = ZoomMode.Enabled, MinZoomFactor = .25f, MaxZoomFactor = 4f,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly ComboBox _languages = new() { MinWidth = 130 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detected = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressRing _loading = new() { Width = 18, Height = 18 };
    private readonly StackPanel _lines = new() { Spacing = 4 };
    private readonly TextBox _selectionText = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxHeight = 115 };
    private readonly TextBox _fullText = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, MaxHeight = 200 };
    private readonly Button _copySelection = new(), _copyAll = new(), _saveText = new();
    private readonly Button _reveal = new() { Visibility = Visibility.Collapsed };
    private string? _savedPath;
    private OcrTextLayout? _layout;
    private int _anchor = -1, _focus = -1;
    private bool _dragging;
    private byte[]? _pixels;
    private string _selected = "";
    private string? _detectedLanguage;
    private bool _started;
    private int _width, _height;
    private double _zoom = 1;
    private static string T(string key) => UiText.OcrReader(key);
    protected internal override Control? InitialFocus => _languages;

    public OcrReaderView(ContextSnapshot snapshot, Func<string?, CancellationToken, Task<string?>> exportTextFile)
    {
        _snapshot = snapshot;
        _exportTextFile = exportTextFile;
        Title = T("Title");
        PreferredSize = new Size(1150, 800);
        var root = new Grid { RowSpacing = 8, Padding = new Thickness(16, 0, 16, 12) };
        root.RowDefinitions.Add(new() { Height = new(48) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = new Grid { Padding = new Thickness(8, 0, 120, 0) };
        title.Children.Add(new TextBlock { Text = Title, FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        TitleBar = title; root.Children.Add(title);

        var settings = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        settings.Children.Add(new TextBlock { Text = T("Language"), VerticalAlignment = VerticalAlignment.Center });
        _languages.Items.Add(new ComboBoxItem { Content = T("Auto"), Tag = "" });
        foreach (string language in _recognizer.AvailableLanguages)
            _languages.Items.Add(new ComboBoxItem { Content = LanguageName(language), Tag = language });
        _languages.SelectedIndex = 0;
        _languages.SelectionChanged += (_, _) => { if (_started && _pixels is not null) _ = ReadAsync(); };
        settings.Children.Add(_languages);
        settings.Children.Add(_loading);
        _detected.VerticalAlignment = VerticalAlignment.Center;
        settings.Children.Add(_detected);
        Grid.SetRow(settings, 1); root.Children.Add(settings);

        var body = new Grid { ColumnSpacing = 12 };
        body.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        var imageLayer = new Grid();
        imageLayer.Children.Add(_image);
        imageLayer.Children.Add(_marks);
        imageLayer.Children.Add(_selection);
        imageLayer.Children.Add(_overlay);
        _overlay.PointerPressed += Overlay_PointerPressed;
        _overlay.PointerMoved += Overlay_PointerMoved;
        _overlay.PointerReleased += (_, e) => { _dragging = false; _overlay.ReleasePointerCapture(e.Pointer); };
        _overlay.PointerCaptureLost += (_, _) => _dragging = false;
        _overlay.DoubleTapped += Overlay_DoubleTapped;
        var menu = new MenuFlyout();
        var copyItem = new MenuFlyoutItem { Text = T("CopySelected") };
        copyItem.Click += async (_, _) => await CopyAsync(_selected);
        var copyAllItem = new MenuFlyoutItem { Text = T("CopyAll") };
        copyAllItem.Click += async (_, _) => await CopyAsync(_fullText.Text);
        menu.Items.Add(copyItem); menu.Items.Add(copyAllItem);
        menu.Opening += (_, _) => copyItem.IsEnabled = _copySelection.IsEnabled;
        _overlay.ContextFlyout = menu;
        // Focus moves here on a click in the image so Ctrl+C and Ctrl+A act on the image text.
        _scroll.IsTabStop = true;
        _scroll.KeyDown += Scroll_KeyDown;
        var fitted = new Viewbox { Child = imageLayer, Stretch = Stretch.Uniform };
        _scroll.Content = fitted;
        var imageFrame = new Border { Child = _scroll, CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(90, 120, 130, 140)) };
        imageFrame.SizeChanged += (_, e) => {
            fitted.Width = Math.Max(100, e.NewSize.Width - 4);
            fitted.Height = Math.Max(100, e.NewSize.Height - 4);
        };
        body.Children.Add(imageFrame);
        var side = new Grid { RowSpacing = 8 };
        side.RowDefinitions.Add(new() { Height = GridLength.Auto });
        side.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        side.RowDefinitions.Add(new() { Height = GridLength.Auto });
        side.RowDefinitions.Add(new() { Height = GridLength.Auto });
        side.Children.Add(new TextBlock { Text = T("Help"), TextWrapping = TextWrapping.Wrap });
        var rows = new ScrollViewer { Content = _lines, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(rows, 1); side.Children.Add(rows);
        var label = new TextBlock { Text = T("SelectedText") };
        Grid.SetRow(label, 2); side.Children.Add(label);
        Grid.SetRow(_selectionText, 3); side.Children.Add(_selectionText);
        Grid.SetColumn(side, 1); body.Children.Add(side);
        Grid.SetRow(body, 2); root.Children.Add(body);

        var footer = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var zoomOut = new Button { Content = T("ZoomOut") };
        var zoomIn = new Button { Content = T("ZoomIn") };
        zoomOut.Click += (_, _) => Zoom(Math.Max(.25, _zoom / 1.25));
        zoomIn.Click += (_, _) => Zoom(Math.Min(4, _zoom * 1.25));
        actions.Children.Add(zoomOut); actions.Children.Add(zoomIn);
        _copySelection.Content = T("CopySelected"); _copyAll.Content = T("CopyAll");
        _copySelection.IsEnabled = _copyAll.IsEnabled = false;
        _copySelection.Click += async (_, _) => await CopyAsync(_selected);
        _copyAll.Click += async (_, _) => await CopyAsync(_fullText.Text);
        actions.Children.Add(_copySelection); actions.Children.Add(_copyAll);
        _saveText.Content = T("SaveTxt"); _saveText.IsEnabled = false;
        _saveText.Click += async (_, _) => await SaveAsync(); actions.Children.Add(_saveText);
        _reveal.Content = UiText.ResultShowInExplorer;
        _reveal.Click += (_, _) => Reveal(); actions.Children.Add(_reveal);
        Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        footer.Children.Add(_status);
        var all = new Expander { Header = T("AllText"), Content = _fullText };
        Grid.SetRow(all, 1); Grid.SetColumnSpan(all, 2); footer.Children.Add(all);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;

        Closed += (_, _) => {
            _closing.Cancel(); _run?.Cancel();
            _pixels = null; _image.Source = null;
            Clear();
        };
        Loaded += async (_, _) => {
            if (_started) return;
            _started = true;
            await LoadAsync(imageLayer);
        };
    }

    private async Task LoadAsync(Grid layer)
    {
        _status.Text = T("Loading");
        _loading.IsActive = true;
        try
        {
            if (_snapshot.IsStale || _snapshot.Content is not FileSelection { Files.Count: 1 } files ||
                files.Files[0].Path is not { } path)
                throw new InvalidDataException();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 100_000_000) throw new InvalidDataException();
            using var random = stream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(random);
            _width = checked((int)decoder.PixelWidth);
            _height = checked((int)decoder.PixelHeight);
            if (_width < 1 || _height < 1 || (long)_width * _height > 20_000_000 ||
                _width > 30000 || _height > 30000)
                throw new InvalidDataException();
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var pixels = new byte[checked(_width * _height * 4)];
            bitmap.CopyToBuffer(pixels.AsBuffer());
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            _closing.Token.ThrowIfCancellationRequested();
            _pixels = pixels;
            _image.Source = source;
            _image.Width = _marks.Width = _selection.Width = _overlay.Width = layer.Width = _width;
            _image.Height = _marks.Height = _selection.Height = _overlay.Height = layer.Height = _height;
            await ReadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!IsClosed) _status.Text = T("ImageUnavailable");
        }
        finally { if (!IsClosed) _loading.IsActive = false; }
    }

    private async Task ReadAsync()
    {
        if (_pixels is null || IsClosed) return;
        _run?.Cancel();
        var current = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _run = current;
        var token = current.Token;
        Clear();
        _status.Text = T("Recognizing");
        _loading.IsActive = true;
        string? language = (_languages.SelectedItem as ComboBoxItem)?.Tag as string;
        try
        {
            var result = await _recognizer.RecognizePageAsync(_width, _height, _pixels,
                string.IsNullOrEmpty(language) ? null : language, token);
            if (token.IsCancellationRequested || IsClosed) return;
            _detectedLanguage = result.Language;
            _detected.Text = result.Language is null ? "" :
                UiText.Format(T("DetectedLanguage"), LanguageName(result.Language));
            Render(result.Lines);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!IsClosed && !token.IsCancellationRequested) _status.Text = T("RecognitionFailed");
        }
        finally
        {
            if (ReferenceEquals(_run, current))
            {
                _run = null;
                if (!IsClosed) _loading.IsActive = false;
            }
            current.Dispose();
        }
    }

    private void Render(IReadOnlyList<RecognizedLine> lines)
    {
        _fullText.Text = string.Join(Environment.NewLine, lines.Select(x => x.Text));
        _copyAll.IsEnabled = !string.IsNullOrWhiteSpace(_fullText.Text);
        _saveText.IsEnabled = _copyAll.IsEnabled;
        if (!_copyAll.IsEnabled) { _status.Text = T("NoText"); return; }
        _layout = new OcrTextLayout(lines);
        for (int i = 0; i < lines.Count && _lines.Children.Count < 600; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            var chooseLine = new Button {
                Content = new TextBlock { Text = line.Text, TextWrapping = TextWrapping.Wrap },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            int source = i;
            chooseLine.Click += (_, _) =>
            {
                if (_layout?.LineRange(source) is var (first, last)) SelectRange(first, last);
                else Select(line.Text);
            };
            AutomationProperties.SetName(chooseLine, line.Text);
            _lines.Children.Add(chooseLine);
        }
        var mark = new SolidColorBrush(ColorHelper.FromArgb(40, 35, 145, 225));
        for (int i = 0; i < Math.Min(_layout.Count, MaxMarks); i++)
            _marks.Children.Add(Mark(_layout.Box(i), mark));
        _status.Text = _layout.Count > MaxMarks ? T("OverlayLimited") :
            UiText.Format(T("Found"), lines.Count);
    }

    private const int MaxMarks = 2400;

    private static Rectangle Mark(OcrBox box, Brush fill)
    {
        // Corners follow the line height so small and large text get the same shape.
        double radius = Math.Min(box.Height * .2, 8);
        var shape = new Rectangle { Width = Math.Max(box.Width, 1), Height = Math.Max(box.Height, 1),
            Fill = fill, RadiusX = radius, RadiusY = radius };
        Canvas.SetLeft(shape, box.X);
        Canvas.SetTop(shape, box.Y);
        return shape;
    }

    private void Overlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_overlay);
        if (_layout is null || !point.Properties.IsLeftButtonPressed) return;
        int word = _layout.HitTest(point.Position.X, point.Position.Y);
        if (word < 0) return;
        _scroll.Focus(FocusState.Pointer);
        // Capture keeps the drag going outside the image; without it the drag still works inside.
        _overlay.CapturePointer(e.Pointer);
        _dragging = true;
        SelectRange(word, word);
        e.Handled = true;
    }

    private void Overlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _layout is null) return;
        var position = e.GetCurrentPoint(_overlay).Position;
        int word = _layout.HitTest(position.X, position.Y);
        if (word >= 0 && word != _focus) SelectRange(_anchor, word);
    }

    private void Overlay_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_layout is null) return;
        var position = e.GetPosition(_overlay);
        int word = _layout.HitTest(position.X, position.Y);
        if (word < 0) return;
        var (first, last) = _layout.LineOf(word);
        SelectRange(first, last);
    }

    private void Scroll_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!control || _layout is not { Count: > 0 }) return;
        if (e.Key == VirtualKey.C && _copySelection.IsEnabled) { _ = CopyAsync(_selected); e.Handled = true; }
        else if (e.Key == VirtualKey.A) { SelectRange(0, _layout.Count - 1); e.Handled = true; }
    }

    private void SelectRange(int from, int to)
    {
        if (_layout is null) return;
        _anchor = from; _focus = to;
        _selection.Children.Clear();
        var fill = new SolidColorBrush(ColorHelper.FromArgb(110, 35, 145, 225));
        foreach (var box in _layout.SelectionBoxes(from, to))
            _selection.Children.Add(Mark(box, fill));
        Select(_layout.Text(from, to));
    }

    private void Select(string text)
    {
        _selected = text;
        _selectionText.Text = text;
        _copySelection.IsEnabled = !string.IsNullOrWhiteSpace(text);
    }

    private void Clear()
    {
        _marks.Children.Clear();
        _selection.Children.Clear();
        _lines.Children.Clear();
        _layout = null;
        _anchor = _focus = -1;
        _dragging = false;
        _fullText.Text = _selectionText.Text = _selected = "";
        _detected.Text = "";
        _copyAll.IsEnabled = _copySelection.IsEnabled = _saveText.IsEnabled = false;
        _detectedLanguage = null;
    }

    private void Zoom(double requested)
    {
        _zoom = requested;
        _scroll.ChangeView(null, null, (float)_zoom, false);
    }

    private async Task CopyAsync(string text)
    {
        if (IsClosed || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            await new ClipboardWriter().WriteTextAsync(text);
            if (!IsClosed) _status.Text = T("Copied");
        }
        catch (Exception)
        {
            if (!IsClosed) _status.Text = T("CopyFailed");
        }
    }

    /// <summary>A canvas that shows the text cursor, like selectable text.</summary>
    private sealed partial class TextCanvas : Canvas
    {
        public TextCanvas() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    }
}
