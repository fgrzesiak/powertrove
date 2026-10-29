using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PowerTrove.Desktop;

/// <summary>Live options over the existing action. Rendering stays inside the pack worker.</summary>
public sealed class ImageEffectsWindow : HostedView, IVisualActionEditor
{
    private readonly ContextSnapshot snapshot;
    private readonly Func<ActionParameters, CancellationToken, Task<ActionPreview?>> validate;
    private readonly Func<bool> stillCurrent;
    private readonly Dictionary<string, string> values;
    private readonly CancellationTokenSource closing = new();
    private CancellationTokenSource? refresh;
    private readonly TaskCompletionSource<DictionaryParameters?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StackPanel controls = new() { Spacing = 12 };
    private readonly ContentControl controlsHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
    private readonly Image picture = new() { Stretch = Stretch.Fill };
    private readonly Grid previewSurface = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock warnings = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock dimensions = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button apply = new() { IsEnabled = false };
    private readonly List<Action> syncControls = new();
    private readonly bool resize;
    private ImageResizeDimensions? resizeDimensions;
    private readonly Dictionary<string, NumberBox> resizeEditors = new();
    private bool resizeSync, resizeInputInvalid;
    private Slider? resizeScale;
    private TextBlock? resizePercentage;
    private bool closed, saving, refreshing;
    private long revision;
    private DictionaryParameters? result;
    HostedView IVisualActionEditor.HostView => this;
    // The back arrow leads to the action's options, like the "Options" button.
    public override bool CanGoBack => true;
    public override void GoBack() { ReturnToOptions = true; Close(); }
    public bool ReturnToOptions { get; private set; }
    public DictionaryParameters DraftParameters => new(new Dictionary<string, string>(values));
    private static string T(string key) => UiText.ImageEditor(key);

    public ImageEffectsWindow(ContextSnapshot snapshot, ActionDescriptor descriptor, DictionaryParameters initial,
        Func<ActionParameters, CancellationToken, Task<ActionPreview?>> validate, Func<bool> stillCurrent)
    {
        this.snapshot = snapshot; this.validate = validate; this.stillCurrent = stillCurrent;
        resize = descriptor.Id == "powertrove.image.resize";
        values = (descriptor.Parameters ?? []).ToDictionary(p => p.Name,
            p => initial.Values.TryGetValue(p.Name, out var v) ? v : p.DefaultValue ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        Title = descriptor.Title;
        if (resize) { BuildResizeWindow(descriptor); return; }
        var root = new Grid { Padding = new(20), RowSpacing = 12, Background = new SolidColorBrush(Colors.Transparent) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = descriptor.Title, FontSize = 22 });
        var body = new Grid { ColumnSpacing = 20, RowSpacing = 12 };
        body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new(320) });
        body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); body.RowDefinitions.Add(new() { Height = new(0) });
        var view = new Grid(); view.RowDefinitions.Add(new() { Height = GridLength.Auto }); view.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var caption = new StackPanel { Spacing = 8 };
        caption.Children.Add(new TextBlock { Text = T(resize ? "ResizeHint" : "EffectsHint"), TextWrapping = TextWrapping.Wrap }); caption.Children.Add(dimensions);
        view.Children.Add(caption);
        var backdrop = new Grid { Background = new SolidColorBrush(Colors.DimGray), Margin = new(0, 12, 0, 0) };
        previewSurface.Children.Add(new Image { Source = ImageCanvasWindow.Checker(), Stretch = Stretch.Fill, IsHitTestVisible = false });
        previewSurface.Children.Add(picture);
        backdrop.Children.Add(new Viewbox { Child = previewSurface, Stretch = Stretch.Uniform, Margin = new(16) });
        Grid.SetRow(backdrop, 1); view.Children.Add(backdrop);
        AutomationProperties.SetName(picture, T("EffectsPreview")); body.Children.Add(view);
        foreach (var p in descriptor.Parameters ?? [])
        {
            var editor = p.Name == "Position" && p.Kind == ActionParameterKind.Choice ? PositionEditor(p)
                : p.Kind == ActionParameterKind.Integer ? SliderEditor(p) : ActionOptionsDialog.CreateEditor(p, values, ((App)App.Current).Shell!);
            controls.Children.Add(editor);
            if (editor.Tag is Tuple<Action<Action>, ActionParameterDefinition> tag) tag.Item1(Changed);
        }
        controls.Children.Add(warnings); controlsHost.Content = controls;
        controlsHost.IsEnabled = !resize;
        var sidebar = new ScrollViewer { Content = controlsHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(sidebar, 1); body.Children.Add(sidebar);
        body.SizeChanged += (_, _) => {
            bool narrow = body.ActualWidth < 760;
            body.ColumnDefinitions[1].Width = new(narrow ? 0 : 320); body.RowDefinitions[1].Height = narrow ? GridLength.Auto : new(0);
            Grid.SetColumn(sidebar, narrow ? 0 : 1); Grid.SetRow(sidebar, narrow ? 1 : 0); Grid.SetColumnSpan(sidebar, narrow ? 2 : 1);
            Grid.SetColumnSpan(view, narrow ? 2 : 1); sidebar.MaxHeight = narrow ? Math.Min(280, Math.Max(120, body.ActualHeight * .45)) : double.PositiveInfinity;
        };
        Grid.SetRow(body, 1); root.Children.Add(body);
        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var cancel = CreateCancelButton(); Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        footer.Children.Add(status);
        var back = new Button { Content = T("Back") }; back.Click += (_, _) => { ReturnToOptions = true; Close(); };
        Grid.SetColumn(back, 2); footer.Children.Add(back);
        apply.Content = UiText.OptionsRun; apply.Click += async (_, _) => await ApplyAsync(); Grid.SetColumn(apply, 3); footer.Children.Add(apply);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        root.KeyDown += (_, args) => { if (args.Key == global::Windows.System.VirtualKey.Escape) { args.Handled = true; Close(); } };
        Closed += (_, _) => { closed = true; closing.Cancel(); refresh?.Cancel(); picture.Source = null; completion.TrySetResult(result); };
        PreferredSize = new(1100, 820);
    }

    public Task<DictionaryParameters?> ShowSelectionAsync() { Changed(); return completion.Task; }

    private void BuildResizeWindow(ActionDescriptor descriptor)
    {
        var root = new Grid { Padding = new(20), RowSpacing = 16, Background = new SolidColorBrush(Colors.Transparent) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = descriptor.Title, FontSize = 20 });

        previewSurface.Children.Add(new Image { Source = ImageCanvasWindow.Checker(), Stretch = Stretch.Fill, IsHitTestVisible = false });
        previewSurface.Children.Add(picture);
        AutomationProperties.SetName(picture, T("EffectsPreview"));
        var preview = new Border { CornerRadius = new(8), Background = new SolidColorBrush(Colors.DimGray),
            Child = new Viewbox { Child = previewSurface, Stretch = Stretch.Uniform, Margin = new(24) } };
        Grid.SetRow(preview, 1); root.Children.Add(preview);

        resizePercentage = new TextBlock { FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center };
        controls.Children.Add(resizePercentage);
        resizeScale = new Slider { Minimum = 1, Maximum = 400, Value = 100, StepFrequency = 1, IsThumbToolTipEnabled = false };
        AutomationProperties.SetName(resizeScale, T("ResizeScale"));
        resizeScale.ValueChanged += (_, e) => { if (!resizeSync && resizeDimensions is not null) ResizeEdit(() => resizeDimensions.SetScale(e.NewValue / 100)); };
        controls.Children.Add(resizeScale);
        dimensions.HorizontalAlignment = HorizontalAlignment.Center; controls.Children.Add(dimensions);
        controls.Children.Add(warnings);

        var details = new StackPanel { Spacing = 12 };
        var sizes = new Grid { ColumnSpacing = 12 };
        sizes.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); sizes.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        int column = 0;
        foreach (string key in new[] { "MaxWidth", "MaxHeight" })
        {
            var number = new NumberBox { Header = T(key == "MaxWidth" ? "ResizeWidth" : "ResizeHeight"), Minimum = 1, Maximum = 30000,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
            number.ValueChanged += (_, e) => {
                if (resizeSync || resizeDimensions is null) return;
                if (!double.IsFinite(e.NewValue)) { resizeInputInvalid = true; Changed(); return; }
                ResizeEdit(() => key == "MaxWidth" ? resizeDimensions.SetWidth((int)Math.Round(e.NewValue)) : resizeDimensions.SetHeight((int)Math.Round(e.NewValue)));
            };
            resizeEditors[key] = number;
            Grid.SetColumn(number, column++); sizes.Children.Add(number);
        }
        details.Children.Add(sizes);
        var quality = new CheckBox { Content = T("ResizeImprove"), IsChecked = values.GetValueOrDefault("Resampling") == "smart" };
        ToolTipService.SetToolTip(quality, descriptor.Parameters?.FirstOrDefault(p => p.Name == "Resampling")?.Description);
        quality.Checked += (_, _) => { values["Resampling"] = "smart"; Changed(); };
        quality.Unchecked += (_, _) => { values["Resampling"] = "standard"; Changed(); };
        details.Children.Add(quality);
        var options = new Button { Content = T("Back") };
        options.Click += (_, _) => { ReturnToOptions = true; Close(); }; details.Children.Add(options);
        controls.Children.Add(new Expander { Header = T("ResizeDetails"), Content = details, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch });
        controlsHost.Content = controls; controlsHost.IsEnabled = false;
        Grid.SetRow(controlsHost, 2); root.Children.Add(controlsHost);

        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(status);
        var cancel = CreateCancelButton();
        Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        apply.Content = T("ResizeSave"); apply.Click += async (_, _) => await ApplyAsync(); Grid.SetColumn(apply, 2); footer.Children.Add(apply);
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        root.KeyDown += (_, args) => { if (args.Key == global::Windows.System.VirtualKey.Escape) { args.Handled = true; Close(); } };
        Closed += (_, _) => { closed = true; closing.Cancel(); refresh?.Cancel(); picture.Source = null; completion.TrySetResult(result); };
        PreferredSize = new(640, 700);
    }
    private void Changed()
    {
        if (closed || saving) return;
        if (resizeInputInvalid) { apply.IsEnabled = false; status.Text = T("ResizeInvalid"); return; }
        foreach (var child in controls.Children.OfType<FrameworkElement>())
            if (child.Tag is Tuple<Action<Action>, ActionParameterDefinition> tag && tag.Item2.VisibleWhenParameter is { } name)
                child.Visibility = values.TryGetValue(name, out var value) && value == tag.Item2.VisibleWhenValue ? Visibility.Visible : Visibility.Collapsed;
        revision++;
        apply.IsEnabled = false;
        status.Text = T(picture.Source is null ? "Loading" : "EffectsUpdating");
        // Keep the last completed image visible. One render runs at a time; intermediate edits coalesce.
        if (!refreshing) _ = RefreshAsync();
    }
    private void ClearPreview()
    {
        picture.Source = null; previewSurface.Visibility = Visibility.Collapsed;
        warnings.Text = dimensions.Text = string.Empty; apply.IsEnabled = false;
    }
    private async Task RefreshAsync()
    {
        refreshing = true;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(closing.Token); refresh = cts;
        try
        {
            while (!closed && !saving)
            {
                // Throttle, rather than debounce: a continuous drag still receives completed frames.
                await Task.Delay(40, cts.Token);
                long renderedRevision = revision;
                var draft = new Dictionary<string, string>(values);
                try
                {
                    if (!stillCurrent() || snapshot.IsStale) { ClearPreview(); status.Text = UiText.PackSelectAgain; return; }
                    var preview = await validate(ImagePreviewRequest.Create(draft), cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    if (closed) return;
                    if (!stillCurrent() || snapshot.IsStale) { ClearPreview(); status.Text = UiText.PackSelectAgain; return; }
                    if (preview is { IsBlocked: false, ImagePreview: { } frame } && frame.TryDecodePng(out var bytes) &&
                        (!resize || frame.OriginalWidth.HasValue && frame.OriginalHeight.HasValue))
                    {
                        using var memory = new MemoryStream(bytes, false); using var stream = memory.AsRandomAccessStream();
                        var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream).AsTask(cts.Token);
                        cts.Token.ThrowIfCancellationRequested();
                        if (closed) return;
                        if (!stillCurrent() || snapshot.IsStale) { ClearPreview(); status.Text = UiText.PackSelectAgain; return; }
                        // Swap only after decoding. Completed frames stay ordered even during continuous edits.
                        previewSurface.Width = frame.PixelWidth; previewSurface.Height = frame.PixelHeight;
                        picture.Source = bitmap; previewSurface.Visibility = Visibility.Visible;
                        if (resize && frame is { OriginalWidth: { } ow, OriginalHeight: { } oh } &&
                            (resizeDimensions is null || resizeDimensions.OriginalWidth != ow || resizeDimensions.OriginalHeight != oh))
                        { resizeDimensions = new(ow, oh, frame.SourceWidth, frame.SourceHeight); SyncResize(); controlsHost.IsEnabled = true; }
                        dimensions.Text = resize ? UiText.Format(T("ResizeOutput"), frame.SourceWidth, frame.SourceHeight)
                            : UiText.Format(T("EffectsDimensions"), frame.SourceWidth, frame.SourceHeight);
                        LinkText.Set(warnings, string.Join(Environment.NewLine, preview.Warnings));
                        bool latest = renderedRevision == revision;
                        status.Text = resizeInputInvalid ? T("ResizeInvalid") : latest ? (resize ? string.Empty : preview.PreviewText) : T("EffectsUpdating");
                        apply.IsEnabled = latest && !resizeInputInvalid && preview.Confirmation is null;
                    }
                    else if (renderedRevision == revision)
                    { ClearPreview(); status.Text = preview?.IsBlocked == true ? preview.PreviewText : T("EffectsUnavailable"); }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { if (!closed && renderedRevision == revision) { ClearPreview(); status.Text = T("EffectsUnavailable"); } }
                finally { draft.Clear(); }
                if (renderedRevision == revision) return;
            }
        }
        catch (OperationCanceledException) { }
        finally { refreshing = false; if (ReferenceEquals(refresh, cts)) refresh = null; cts.Dispose(); }
    }
    private async Task ApplyAsync()
    {
        if (resizeInputInvalid) { apply.IsEnabled = false; status.Text = T("ResizeInvalid"); return; }
        if (saving || closed || !stillCurrent() || snapshot.IsStale) { apply.IsEnabled = false; status.Text = UiText.PackSelectAgain; return; }
        saving = true; refresh?.Cancel(); controlsHost.IsEnabled = apply.IsEnabled = false; status.Text = UiText.OptionsChecking;
        try
        {
            var chosen = DraftParameters;
            var preview = await validate(chosen, closing.Token);
            if (closed) return;
            if (preview is { IsBlocked: false, Confirmation: null } && stillCurrent() && !snapshot.IsStale) { result = chosen; Close(); return; }
            status.Text = preview?.PreviewText ?? UiText.PackSelectAgain;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!closed) status.Text = T("EffectsUnavailable"); }
        finally { if (!closed) { saving = false; controlsHost.IsEnabled = true; apply.IsEnabled = false; } }
    }
    private FrameworkElement SliderEditor(ActionParameterDefinition p)
    {
        var panel = new StackPanel { Spacing = 4 };
        var number = new NumberBox { Header = p.Title, Minimum = p.Minimum ?? 0, Maximum = p.Maximum ?? 100,
            Value = int.TryParse(values[p.Name], out var value) ? value : 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        // The adjacent number already shows the value. Avoid delayed slider popups during window teardown.
        var slider = new Slider { Minimum = number.Minimum, Maximum = number.Maximum, StepFrequency = 1, Value = number.Value, IsThumbToolTipEnabled = false };
        AutomationProperties.SetName(slider, p.Title); AutomationProperties.SetName(number, p.Title);
        bool sync = false;
        void Set(double value) {
            if (sync) return;
            if (!double.IsFinite(value)) return;
            sync = true;
            int n = (int)Math.Round(value); number.Value = slider.Value = n; values[p.Name] = n.ToString(CultureInfo.InvariantCulture); sync = false; Changed();
        }
        number.ValueChanged += (_, e) => Set(e.NewValue); slider.ValueChanged += (_, e) => Set(e.NewValue);
        panel.Children.Add(number); panel.Children.Add(slider);
        panel.Tag = new Tuple<Action<Action>, ActionParameterDefinition>(_ => { }, p); return panel;
    }

    private void SyncResize()
    {
        if (resizeDimensions is null) return;
        resizeSync = true;
        if (resizeScale is not null && resizePercentage is not null)
        {
            double percent = 100.0 * resizeDimensions.Width / resizeDimensions.OriginalWidth;
            resizeScale.Maximum = Math.Max(resizeScale.Minimum, Math.Max(percent, 100.0 * resizeDimensions.SliderWidth / resizeDimensions.OriginalWidth));
            resizeScale.Value = percent;
            resizePercentage.Text = UiText.Format(T("ResizePercent"), Math.Round(percent));
        }
        foreach (var (key, value) in new[] { ("MaxWidth", resizeDimensions.Width), ("MaxHeight", resizeDimensions.Height) })
        {
            values[key] = value.ToString(CultureInfo.InvariantCulture);
            if (resizeEditors.TryGetValue(key, out var editor)) editor.Value = value;
        }
        resizeSync = false;
    }
    private void ResizeEdit(Func<bool> change)
    {
        if (saving || closed || resizeDimensions is null) return;
        bool valid = change(); resizeInputInvalid = false; SyncResize();
        if (!valid) { Changed(); status.Text = T("ResizeInvalid"); return; }
        // Entering a larger whole-image size is an explicit request to enlarge it.
        values["AllowUpscale"] = "true"; Changed();
    }
    private FrameworkElement PositionEditor(ActionParameterDefinition p)
    {
        var panel = new StackPanel { Spacing = 4 }; panel.Children.Add(new TextBlock { Text = p.Title });
        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 0; i < 3; i++) { grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); }
        string[] positions = ["top-left", "top", "top-right", "left", "center", "right", "bottom-left", "bottom", "bottom-right"];
        string[] glyphs = ["↖", "↑", "↗", "←", "•", "→", "↙", "↓", "↘"];
        void Pick(string value) { values[p.Name] = value; foreach (var update in syncControls) update(); Changed(); }
        for (int i = 0; i < positions.Length; i++)
        {
            string position = positions[i]; var choice = p.Choices?.FirstOrDefault(c => c.Value == position); if (choice is null) continue;
            var button = new Button { Content = new FontIcon { Glyph = glyphs[i], FontFamily = new("Segoe UI"), FontSize = 16 }, Width = 46, Height = 36 };
            AutomationProperties.SetName(button, choice.Title); ToolTipService.SetToolTip(button, choice.Title);
            syncControls.Add(() => button.BorderThickness = new(values[p.Name] == position ? 3 : 1));
            button.Click += (_, _) => Pick(position); Grid.SetRow(button, i / 3); Grid.SetColumn(button, i % 3); grid.Children.Add(button);
        }
        panel.Children.Add(grid);
        if (p.Choices?.FirstOrDefault(c => c.Value == "tiled") is { } tiled)
        {
            var tile = new CheckBox { Content = tiled.Title, IsChecked = values[p.Name] == "tiled" };
            bool sync = false; syncControls.Add(() => { sync = true; tile.IsChecked = values[p.Name] == "tiled"; sync = false; });
            tile.Checked += (_, _) => { if (!sync) Pick("tiled"); }; tile.Unchecked += (_, _) => { if (!sync && values[p.Name] == "tiled") Pick("center"); }; panel.Children.Add(tile);
        }
        foreach (var update in syncControls) update(); return panel;
    }
}
