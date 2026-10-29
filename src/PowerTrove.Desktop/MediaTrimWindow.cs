using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PowerTrove.Application.Media;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Platform.Windows.Media;

namespace PowerTrove.Desktop;

/// <summary>Read-only playback and range selection; the host exports through the existing action.</summary>
public sealed class MediaTrimWindow : HostedView, IVisualActionEditor
{
    HostedView IVisualActionEditor.HostView => this;
    // The back arrow leads to the action's options, like the "Options" button.
    public override bool CanGoBack => true;
    public override void GoBack() { ReturnToOptions = true; Close(); }
    private readonly ContextSnapshot _snapshot;
    private readonly DictionaryParameters _initial;
    private readonly Func<ActionParameters, CancellationToken, Task<ActionPreview?>> _validate;
    private readonly Func<bool> _stillCurrent;
    private readonly LocalMediaPlayback _preview = new(new FileSignatureVerifier());
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource<DictionaryParameters?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MediaPlayerElement _player = new() { Height = 220, AreTransportControlsEnabled = false, Stretch = Stretch.Uniform };
    private readonly TextBlock _audio = new() { Text = UiText.MediaTrimAudioPreview, Foreground = new SolidColorBrush(Colors.White), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { Text = UiText.MediaTrimLoading, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _position = new();
    private readonly TextBlock _duration = new();
    private readonly Slider _seek = new() { Minimum = 0, StepFrequency = .01 };
    private readonly Slider _start = new() { Minimum = 0, StepFrequency = .01 };
    private readonly Slider _end = new() { Minimum = 0, StepFrequency = .01 };
    private readonly NumberBox _startNumber = new() { Minimum = 0, SmallChange = .1, LargeChange = 1, NumberFormatter = new global::Windows.Globalization.NumberFormatting.DecimalFormatter { IntegerDigits = 1, FractionDigits = 3 }, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 140 };
    private readonly NumberBox _endNumber = new() { Minimum = .001, SmallChange = .1, LargeChange = 1, NumberFormatter = new global::Windows.Globalization.NumberFormatting.DecimalFormatter { IntegerDigits = 1, FractionDigits = 3 }, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 140 };
    private readonly Button _play = new() { Content = UiText.MediaTrimPlay };
    private readonly Button _setStart = new() { Content = UiText.MediaTrimSetStart };
    private readonly Button _setEnd = new() { Content = UiText.MediaTrimSetEnd };
    private readonly Button _apply = new() { Content = UiText.MediaTrimApply, IsEnabled = false };
    private readonly CheckBox _precise = new() { Content = UiText.MediaTrimPrecise };
    private readonly StackPanel _controls = new() { Spacing = 6 };
    private readonly ContentControl _controlHost = new() { IsEnabled = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Grid _band = new() { Height = 10, CornerRadius = new CornerRadius(5) };
    private readonly DispatcherQueueTimer _timer;
    private CancellationTokenSource? _validation;
    private MediaTrimRange? _range;
    private bool _sync;
    private bool _closed;
    private DictionaryParameters? _result;
    public DictionaryParameters DraftParameters => _range?.Parameters() ?? _initial;
    public bool ReturnToOptions { get; private set; }

    public MediaTrimWindow(ContextSnapshot snapshot, DictionaryParameters initial,
        Func<ActionParameters, CancellationToken, Task<ActionPreview?>> validate, Func<bool> stillCurrent)
    {
        _snapshot = snapshot; _initial = initial; _validate = validate; _stillCurrent = stillCurrent;
        Title = UiText.MediaTrimTitle;
        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new Grid { Padding = new Thickness(20, 0, 140, 0) };
        title.Children.Add(new TextBlock { Text = UiText.MediaTrimTitle, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(title); TitleBar = title;
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(20, 12, 20, 12) };
        var surface = new Grid { Background = new SolidColorBrush(Colors.Black), CornerRadius = new CornerRadius(8), MinHeight = 180 };
        surface.Children.Add(_player); surface.Children.Add(_audio); body.Children.Add(surface);
        body.Children.Add(_duration);
        var playback = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        playback.Children.Add(_play); playback.Children.Add(_setStart); playback.Children.Add(_setEnd);
        _controls.Children.Add(playback);
        _controls.Children.Add(_position); _controls.Children.Add(_seek);
        _band.ColumnDefinitions.Add(new ColumnDefinition()); _band.ColumnDefinitions.Add(new ColumnDefinition()); _band.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 3; i++)
        {
            var area = new Border { Background = Brush(i == 1 ? "SystemControlHighlightAccentBrush" : "ControlFillColorSecondaryBrush", i == 1 ? Colors.SeaGreen : Colors.Gray) };
            Grid.SetColumn(area, i); _band.Children.Add(area);
        }
        _controls.Children.Add(_band);
        _controls.Children.Add(RangeRow(UiText.MediaTrimStart, _start, _startNumber));
        _controls.Children.Add(RangeRow(UiText.MediaTrimEnd, _end, _endNumber));
        _controls.Children.Add(_summary);
        var volumeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        volumeRow.Children.Add(new TextBlock { Text = UiText.MediaTrimVolume, VerticalAlignment = VerticalAlignment.Center });
        var volume = new Slider { Minimum = 0, Maximum = 100, Value = 30, Width = 180 };
        AutomationProperties.SetName(volume, UiText.MediaTrimVolume);
        volume.ValueChanged += (_, args) => _preview.Volume = args.NewValue / 100;
        volumeRow.Children.Add(volume); _controls.Children.Add(volumeRow);
        _controls.Children.Add(_precise);
        _controls.Children.Add(new TextBlock { Text = UiText.MediaTrimAccuracy, TextWrapping = TextWrapping.Wrap });
        _controlHost.Content = _controls; body.Children.Add(_controlHost);
        body.Children.Add(new TextBlock { Text = UiText.MediaTrimSaveHint, TextWrapping = TextWrapping.Wrap });
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var footer = new Grid { Padding = new Thickness(20, 12, 20, 18), ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_status);
        var cancel = CreateCancelButton(); Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        var back = new Button { Content = UiText.MediaTrimBack }; back.Click += (_, _) => { ReturnToOptions = true; Close(); };
        Grid.SetColumn(back, 2); footer.Children.Add(back); Grid.SetColumn(_apply, 3); footer.Children.Add(_apply);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root; root.KeyDown += (_, args) => { if (args.Key == global::Windows.System.VirtualKey.Escape) { args.Handled = true; Close(); } };
        PreferredSize = new(940, 820);
        AutomationProperties.SetName(_seek, UiText.MediaTrimPosition);
        AutomationProperties.SetName(_start, UiText.MediaTrimStart); AutomationProperties.SetName(_end, UiText.MediaTrimEnd);
        _start.ValueChanged += (_, args) => EditRange(true, args.NewValue);
        _end.ValueChanged += (_, args) => EditRange(false, args.NewValue);
        _startNumber.ValueChanged += (_, args) => EditRange(true, args.NewValue);
        _endNumber.ValueChanged += (_, args) => EditRange(false, args.NewValue);
        _precise.Checked += (_, _) => PrecisionChanged(); _precise.Unchecked += (_, _) => PrecisionChanged();
        _seek.ValueChanged += (_, args) =>
        {
            if (_sync || _range is null || _closed) return;
            TryPlayback(() =>
            {
                _range.SetPosition(TimeSpan.FromSeconds(args.NewValue));
                if (_range.Position < _range.Start || _range.Position >= _range.End) _preview.Pause();
                _preview.Seek(_range.Position); UpdatePosition();
            });
        };
        _play.Click += (_, _) => TryPlayback(() =>
        {
            if (_range is null) return;
            if (_preview.IsPlaying) _preview.Pause();
            else { _range.SetPosition(_preview.Position); _preview.Seek(_range.PreparePlayback()); _preview.Play(); }
            UpdatePosition();
        });
        _setStart.Click += (_, _) => EditRange(true, _preview.Position.TotalSeconds);
        _setEnd.Click += (_, _) => EditRange(false, _preview.Position.TotalSeconds);
        _apply.Click += (_, _) =>
        {
            if (!_apply.IsEnabled || _range is null || _snapshot.IsStale || !_stillCurrent()) return;
            _result = _range.Parameters(); Close();
        };
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) =>
        {
            if (_closed || _range is null) return;
            if (_snapshot.IsStale || !_stillCurrent()) { ShowUnavailable(UiText.PackSelectAgain); return; }
            TryPlayback(() =>
            {
                var position = _preview.Position;
                if (_preview.IsPlaying && _range.ReachedEnd(position)) { _preview.Pause(); _preview.Seek(_range.End); position = _range.End; }
                _range.SetPosition(position); UpdatePosition();
            });
        };
        _preview.Failed += (_, _) => DispatcherQueue.TryEnqueue(() => { if (!_closed) ShowUnavailable(UiText.MediaTrimPreviewFailed); });
        Closed += (_, _) => Cleanup();
    }

    public Task<DictionaryParameters?> ShowSelectionAsync()
    {
        _ = LoadAsync(); return _completion.Task;
    }
    private async Task LoadAsync()
    {
        try
        {
            if (!MediaTrimRange.CanEdit(_snapshot) || !_stillCurrent()) { ShowUnavailable(UiText.PackSelectAgain); return; }
            var file = ((FileSelection)_snapshot.Content).Files[0];
            var info = await _preview.OpenAsync(file.Path!, _closing.Token);
            if (_closed) return;
            _range = new(info.Duration, _initial.Values);
            _player.SetMediaPlayer(_preview.Player);
            _audio.Visibility = info.HasVideo ? Visibility.Collapsed : Visibility.Visible;
            _duration.Text = UiText.Format(UiText.MediaTrimDuration, Stamp(info.Duration));
            _controlHost.IsEnabled = true;
            _sync = true; _precise.IsChecked = _range.Precise; _sync = false;
            _preview.Seek(_range.Start); UpdateRange(); _timer.Start();
            if (_range.InitialRangeAdjusted) _status.Text = UiText.MediaTrimRangeAdjusted;
            await ValidateAsync();
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_closed) ShowUnavailable(UiText.MediaTrimPreviewFailed); }
    }
    private void EditRange(bool start, double seconds)
    {
        if (_sync || _range is null || _closed || !double.IsFinite(seconds)) return;
        TryPlayback(() =>
        {
            _preview.Pause();
            if (start) _range.SetStart(seconds); else _range.SetEnd(seconds);
            _range.SetPosition(start ? _range.Start : _range.End); _preview.Seek(_range.Position);
            UpdateRange(); _ = ValidateAsync();
        });
    }
    private void PrecisionChanged()
    {
        if (_sync || _range is null || _closed) return;
        _range.Precise = _precise.IsChecked == true; _ = ValidateAsync();
    }
    private async Task ValidateAsync()
    {
        _validation?.Cancel(); _validation?.Dispose();
        var cts = _validation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _apply.IsEnabled = false;
        if (_range is null || _closed) return;
        var parameters = _range.Parameters();
        _status.Text = UiText.OptionsChecking;
        try
        {
            await Task.Delay(200, cts.Token);
            var preview = await _validate(parameters, cts.Token);
            if (cts.IsCancellationRequested || _closed) return;
            bool valid = preview is { IsBlocked: false, Confirmation: null } && !_snapshot.IsStale && _stillCurrent();
            _apply.IsEnabled = valid;
            _status.Text = valid ? (_range.InitialRangeAdjusted ? UiText.MediaTrimRangeAdjusted : UiText.MediaTrimReady) :
                preview?.Confirmation is not null ? UiText.MediaTrimNeedsConfirmation : preview?.PreviewText ?? UiText.OptionsSelectionGone;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!cts.IsCancellationRequested && !_closed) _status.Text = UiText.OptionsSelectionGone; }
    }
    private void UpdateRange()
    {
        if (_range is null) return;
        _sync = true;
        double duration = _range.Duration.TotalSeconds, start = _range.Start.TotalSeconds, end = _range.End.TotalSeconds;
        _seek.Maximum = duration; _start.Maximum = end - .001; _end.Maximum = duration; _end.Minimum = start + .001;
        _startNumber.Maximum = end - .001; _endNumber.Maximum = duration; _endNumber.Minimum = start + .001;
        _start.Value = start; _end.Value = end; _startNumber.Value = start; _endNumber.Value = end;
        _band.ColumnDefinitions[0].Width = new GridLength(start, GridUnitType.Star);
        _band.ColumnDefinitions[1].Width = new GridLength(end - start, GridUnitType.Star);
        _band.ColumnDefinitions[2].Width = new GridLength(Math.Max(0, duration - end), GridUnitType.Star);
        _summary.Text = UiText.Format(UiText.MediaTrimSelection, Stamp(_range.Start), Stamp(_range.End), Stamp(_range.Length));
        _sync = false; UpdatePosition();
    }
    private void UpdatePosition()
    {
        if (_range is null) return;
        _sync = true; _seek.Value = _range.Position.TotalSeconds; _sync = false;
        _position.Text = UiText.Format(UiText.MediaTrimPositionValue, Stamp(_range.Position));
        _play.Content = _preview.IsPlaying ? UiText.MediaTrimPause : UiText.MediaTrimPlay;
        _setStart.IsEnabled = _range.Position < _range.End;
        _setEnd.IsEnabled = _range.Position > _range.Start;
    }
    private void TryPlayback(Action operation)
    {
        try { operation(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_closed) ShowUnavailable(UiText.MediaTrimPreviewFailed); }
    }
    private void ShowUnavailable(string text)
    {
        _validation?.Cancel(); _timer.Stop(); _apply.IsEnabled = false; _controlHost.IsEnabled = false; _status.Text = text;
        try { _preview.Pause(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private void Cleanup()
    {
        if (_closed) return; _closed = true; _closing.Cancel(); _validation?.Cancel(); _validation?.Dispose(); _timer.Stop();
        try { _player.SetMediaPlayer(null); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _preview.Dispose(); _completion.TrySetResult(_result);
    }
    private static Grid RangeRow(string label, Slider slider, NumberBox number)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel(); left.Children.Add(new TextBlock { Text = label }); left.Children.Add(slider); row.Children.Add(left);
        number.Header = UiText.MediaTrimSeconds; AutomationProperties.SetName(number, label);
        Grid.SetColumn(number, 1); row.Children.Add(number); return row;
    }
    private static Brush Brush(string resource, global::Windows.UI.Color fallback) =>
        Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(resource, out var value) && value is Brush brush ? brush : new SolidColorBrush(fallback);
    private static string Stamp(TimeSpan time) => ((int)time.TotalHours).ToString("00", CultureInfo.CurrentCulture) + time.ToString(@"\:mm\:ss\.fff", CultureInfo.CurrentCulture);
}
