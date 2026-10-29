using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PowerTrove.Desktop;

public sealed record ActionOptionsResult(DictionaryParameters Parameters, bool Remember, bool EditMediaVisually = false, bool EditImageVisually = false, bool EditEffectsVisually = false);

/// <summary>
/// Generic options and confirmation page generated from an action's declared
/// <see cref="ActionParameterDefinition"/>s, shown in the main window in place of the palette.
/// Shows the live preview (counts and warnings only; rename names are confined to this page) and
/// disables "Run" while the action reports a blocked preview.
/// </summary>
public static class ActionOptionsDialog
{
    public static async Task<ActionOptionsResult?> ShowAsync(
        Window window,
        ActionDescriptor descriptor,
        IReadOnlyDictionary<string, string>? savedValues,
        Func<ActionParameters, CancellationToken, Task<ActionPreview?>> preview,
        Action? openVisualEditor = null,
        bool allowMediaEditor = false,
        IReadOnlyDictionary<string, string>? initialValues = null,
        bool allowImageEditor = false,
        bool allowEffectsEditor = false)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in descriptor.Parameters ?? Array.Empty<ActionParameterDefinition>())
        {
            values[parameter.Name] = initialValues != null && initialValues.TryGetValue(parameter.Name, out var draft) ? draft :
                descriptor.CanRememberParameters && savedValues != null && savedValues.TryGetValue(parameter.Name, out var saved)
                ? saved
                : parameter.DefaultValue ?? string.Empty;
        }

        var previewText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        var warningsText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("SystemFillColorCautionBrush", out var caution) && caution is Brush cautionBrush)
            warningsText.Foreground = cautionBrush;
        var remember = new CheckBox { Content = UiText.OptionsRemember, Margin = new Thickness(0, 12, 0, 0) };

        var renameTable = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
        var consent = new CheckBox { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        ActionConfirmation? confirmation = null;
        bool validPreview = false;
        var panel = new StackPanel { MinWidth = 360 };
        bool openVisual = false, ran = false;
        ActionOptionsView? view = null;
        if (openVisualEditor != null || allowMediaEditor || allowImageEditor || allowEffectsEditor)
        {
            // Second, visual way to do the same: pages as thumbnails instead of typed numbers.
            var visual = new HyperlinkButton
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { new FontIcon { Glyph = "\uE8A9", FontSize = 14 }, new TextBlock { Text = allowEffectsEditor ? UiText.ImageEditor("EffectsOpen") : allowImageEditor ? UiText.ImageEditor("Open") : allowMediaEditor ? UiText.MediaTrimOpenVisual : UiText.OrganizerOpenVisual } }
                },
                Margin = new Thickness(-10, 0, 0, 8)
            };
            visual.Click += (_, _) => { openVisual = true; view?.Close(); };
            panel.Children.Add(visual);
        }
        var selectionEditors = new Dictionary<string, MultiSelectParameterEditor>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in descriptor.Parameters ?? Array.Empty<ActionParameterDefinition>())
        {
            if (parameter.Editor == ActionParameterEditor.MultiSelect)
            {
                var editor = new MultiSelectParameterEditor(parameter, values);
                selectionEditors.Add(parameter.Name, editor); panel.Children.Add(editor.Root);
            }
            else panel.Children.Add(CreateEditor(parameter, values, window));
        }
        panel.Children.Add(previewText);
        panel.Children.Add(warningsText);
        panel.Children.Add(renameTable);
        panel.Children.Add(consent);
        if (descriptor.CanRememberParameters && descriptor.Parameters is { Count: > 0 }) panel.Children.Add(remember);

        var run = new Button { Content = UiText.OptionsRun, IsEnabled = false };
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style accentStyle)
            run.Style = accentStyle;
        view = new ActionOptionsView(descriptor.Title, panel, run, () => { ran = true; view?.Close(); });

        CancellationTokenSource? refresh = null;
        async Task RefreshAsync()
        {
            refresh?.Cancel();
            if (confirmation is not null) values.Remove(confirmation.ParameterName);
            confirmation = null; validPreview = false;
            consent.IsChecked = false; consent.Visibility = Visibility.Collapsed;
            renameTable.Children.Clear();
            var cts = refresh = new CancellationTokenSource();
            previewText.Text = UiText.OptionsChecking;
            run.IsEnabled = false;
            if (selectionEditors.Values.Any(editor => !editor.IsWithinLimit))
            {
                previewText.Text = UiText.OptionsSelectionTooLarge;
                return;
            }
            try
            {
                await Task.Delay(150, cts.Token);
                var result = await preview(new DictionaryParameters(new Dictionary<string, string>(values)), cts.Token);
                if (cts.IsCancellationRequested) return;
                if (result is null)
                {
                    previewText.Text = UiText.OptionsSelectionGone;
                    warningsText.Text = string.Empty;
                    return;
                }

                bool selectionChanged = false;
                foreach (var (name, editor) in selectionEditors)
                    selectionChanged |= editor.ApplyChoices(result.ParameterChoices?.FirstOrDefault(set => string.Equals(set.ParameterName, name, StringComparison.OrdinalIgnoreCase)));
                if (selectionChanged) { _ = RefreshAsync(); return; }

                string outputs = result.ExpectedOutputs.Count switch
                {
                    0 => string.Empty,
                    1 => " " + UiText.OptionsOneOutput,
                    var n => " " + UiText.Format(UiText.OptionsManyOutputs, n)
                };
                previewText.Text = (result.PreviewText ?? string.Empty) + (result.IsBlocked ? string.Empty : outputs);
                LinkText.Set(warningsText, string.Join(Environment.NewLine, result.Warnings));
                if (result.RenameRows is { Count: > 0 } rows && descriptor.Id == "powertrove.files.rename")
                {
                    Grid Row(string oldName, string newName)
                    {
                        var grid = new Grid { ColumnSpacing = 16 };
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        var oldText = new TextBlock { Text = oldName, TextWrapping = TextWrapping.Wrap };
                        var newText = new TextBlock { Text = newName, TextWrapping = TextWrapping.Wrap };
                        Grid.SetColumn(newText, 1); grid.Children.Add(oldText); grid.Children.Add(newText); return grid;
                    }
                    renameTable.Children.Add(Row(UiText.RenameOldName, UiText.RenameNewName));
                    foreach (var row in rows.Take(1000)) renameTable.Children.Add(Row(row.OldName, row.NewName));
                }
                confirmation = result.Confirmation;
                consent.Content = confirmation?.Prompt;
                consent.Visibility = confirmation is null ? Visibility.Collapsed : Visibility.Visible;
                validPreview = !result.IsBlocked;
                run.IsEnabled = validPreview && confirmation is null;
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!cts.IsCancellationRequested)
                {
                    previewText.Text = UiText.OptionsSelectionGone;
                    warningsText.Text = string.Empty;
                    renameTable.Children.Clear();
                    run.IsEnabled = false;
                }
            }
        }

        consent.Checked += (_, _) => run.IsEnabled = validPreview;
        consent.Unchecked += (_, _) => run.IsEnabled = validPreview && confirmation is null;

        void UpdateVisibility()
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement el && el.Tag is Tuple<Action<Action>, ActionParameterDefinition> tag)
                {
                    var p = tag.Item2;
                    if (p.VisibleWhenParameter != null)
                    {
                        if (!values.TryGetValue(p.VisibleWhenParameter, out var v))
                        {
                            var dependentParam = descriptor.Parameters?.FirstOrDefault(dp => dp.Name == p.VisibleWhenParameter);
                            v = dependentParam?.DefaultValue ?? string.Empty;
                        }
                        bool visible = string.Equals(v, p.VisibleWhenValue, StringComparison.OrdinalIgnoreCase);
                        el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
        }

        foreach (var editor in panel.Children.OfType<FrameworkElement>())
        {
            if (editor.Tag is Tuple<Action<Action>, ActionParameterDefinition> tag)
            {
                tag.Item1(() => { UpdateVisibility(); _ = RefreshAsync(); });
            }
        }

        UpdateVisibility();
        _ = RefreshAsync();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.Closed += (_, _) => closed.TrySetResult();
        ((MainWindow)window).ShowView(view);
        await closed.Task;
        refresh?.Cancel();
        renameTable.Children.Clear();

        if (openVisual)
        {
            if (allowEffectsEditor)
                return new ActionOptionsResult(new DictionaryParameters(values), false, EditEffectsVisually: true);
            if (allowImageEditor)
                return new ActionOptionsResult(new DictionaryParameters(values), false, EditImageVisually: true);
            if (allowMediaEditor)
                return new ActionOptionsResult(new DictionaryParameters(values), descriptor.CanRememberParameters && remember.IsChecked == true, true);
            openVisualEditor!();
            return null;
        }

        if (!ran)
        {
            values.Clear();
            return null;
        }

        if (!validPreview || confirmation is not null && consent.IsChecked != true)
        {
            values.Clear(); return null;
        }
        if (confirmation is not null) values[confirmation.ParameterName] = confirmation.Token;
        return new ActionOptionsResult(new DictionaryParameters(values), descriptor.CanRememberParameters && remember.IsChecked == true);
    }

    private static readonly string[] PresetColors =
        ["#FFFFFF", "#000000", "#7F7F7F", "#D13438", "#F7630C", "#FFB900", "#107C10", "#0078D4", "#5C2D91", "#E3008C"];

    /// <summary>
    /// A color option: a compact dropdown (swatch and hex value) that opens a color field with hue slider, preset
    /// colors and a hex field, and an eyedropper next to it that picks any pixel on screen, including the preview.
    /// </summary>
    private static FrameworkElement ColorEditor(ActionParameterDefinition parameter, Dictionary<string, string> values,
        Window window, out Action<Action> subscribe)
    {
        Action? notify = null;
        static Windows.UI.Color? Parse(string? text)
        {
            string h = (text ?? string.Empty).Trim().TrimStart('#');
            if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
            return h.Length == 6 && uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
                ? Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
                : null;
        }
        static string Hex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        static Brush Theme(string key) => (Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];

        var current = Parse(values.TryGetValue(parameter.Name, out var v) ? v : null)
            ?? Parse(parameter.DefaultValue) ?? Microsoft.UI.Colors.White;

        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            BorderBrush = Theme("ControlStrokeColorDefaultBrush"), Background = new SolidColorBrush(current) };
        var hexLabel = new TextBlock { Text = Hex(current), VerticalAlignment = VerticalAlignment.Center };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(swatch);
        label.Children.Add(hexLabel);

        var spectrum = new ColorPicker
        {
            ColorSpectrumShape = ColorSpectrumShape.Box, IsMoreButtonVisible = false, IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = false, IsHexInputVisible = false, IsColorPreviewVisible = false,
            IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false, Color = current,
        };
        var hexBox = new TextBox { Text = Hex(current), Width = 120, MaxLength = 7, Header = UiText.ColorHex, HorizontalAlignment = HorizontalAlignment.Left };
        var presets = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 10, ItemWidth = 30, ItemHeight = 30 };
        bool syncing = false;

        void Apply(Windows.UI.Color color, object? source)
        {
            if (syncing) return;
            syncing = true;
            try
            {
                current = color;
                swatch.Background = new SolidColorBrush(color);
                hexLabel.Text = Hex(color);
                if (!ReferenceEquals(source, hexBox)) hexBox.Text = Hex(color);
                if (!ReferenceEquals(source, spectrum)) spectrum.Color = color;
                values[parameter.Name] = Hex(color);
            }
            finally { syncing = false; }
            notify?.Invoke();
        }

        foreach (var preset in PresetColors)
        {
            var color = Parse(preset)!.Value;
            var chip = new Button
            {
                Width = 24, Height = 24, Padding = new Thickness(0), CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(color), BorderBrush = Theme("ControlStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
            };
            chip.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(color);
            chip.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(color);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, preset);
            chip.Click += (_, _) => Apply(color, chip);
            presets.Children.Add(chip);
        }
        spectrum.ColorChanged += (_, args) => Apply(args.NewColor, spectrum);
        hexBox.TextChanged += (_, _) => { if (Parse(hexBox.Text) is { } typed) Apply(typed, hexBox); };
        hexBox.LostFocus += (_, _) => hexBox.Text = Hex(current);

        var panel = new StackPanel { Spacing = 12, Width = 300 };
        panel.Children.Add(spectrum);
        panel.Children.Add(presets);
        panel.Children.Add(hexBox);
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16)));
        presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        var dropdown = new DropDownButton
        {
            Content = label,
            Flyout = new Flyout { Content = panel, FlyoutPresenterStyle = presenter, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dropdown, parameter.Title);

        // Picks any pixel on screen; PowerTrove stays visible, so its preview can be picked from too.
        var pick = new Button { Content = new FontIcon { Glyph = "\uEF3C", FontSize = 14 }, Padding = new Thickness(9, 6, 9, 6) };
        ToolTipService.SetToolTip(pick, UiText.ColorPickFromScreen);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pick, UiText.ColorPickFromScreen);
        pick.Click += async (_, _) =>
        {
            pick.IsEnabled = false;
            try
            {
                if (await MainWindow.PickScreenColorAsync(window) is { } picked)
                    Apply(Windows.UI.Color.FromArgb(255, picked.R, picked.G, picked.B), pick);
            }
            finally { pick.IsEnabled = true; }
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(dropdown);
        row.Children.Add(pick);
        var root = new StackPanel { Margin = new Thickness(0, 8, 0, 8), Spacing = 4 };
        root.Children.Add(new TextBlock { Text = parameter.Title });
        root.Children.Add(row);
        subscribe = changed => notify = changed;
        return root;
    }

    internal static FrameworkElement CreateEditor(ActionParameterDefinition parameter, Dictionary<string, string> values, Window window)
    {
        FrameworkElement editor;
        Action<Action> subscribe;
        switch (parameter.Kind)
        {
            case ActionParameterKind.FilePath:
            {
                var grid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    },
                    Margin = new Thickness(0, 8, 0, 8)
                };

                var textBox = new TextBox
                {
                    Header = parameter.Title,
                    Text = values.TryGetValue(parameter.Name, out var v) ? v : string.Empty,
                    PlaceholderText = parameter.Placeholder ?? UiText.OptionsPathPlaceholder,
                    Margin = new Thickness(0, 0, 8, 0)
                };

                var button = new Button
                {
                    Content = UiText.OptionsBrowse,
                    VerticalAlignment = VerticalAlignment.Bottom
                };

                Grid.SetColumn(textBox, 0);
                Grid.SetColumn(button, 1);

                grid.Children.Add(textBox);
                grid.Children.Add(button);

                subscribe = changed =>
                {
                    textBox.TextChanged += (_, _) =>
                    {
                        values[parameter.Name] = textBox.Text;
                        changed();
                    };

                    button.Click += async (_, _) =>
                    {
                        var picker = new Windows.Storage.Pickers.FileOpenPicker();
                        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                        if (parameter.FileTypes != null && parameter.FileTypes.Count > 0)
                        {
                            foreach (var ext in parameter.FileTypes)
                            {
                                picker.FileTypeFilter.Add(ext.StartsWith(".") ? ext : "." + ext);
                            }
                        }
                        else
                        {
                            picker.FileTypeFilter.Add("*");
                        }

                        var file = await picker.PickSingleFileAsync();
                        if (file != null)
                        {
                            textBox.Text = file.Path;
                        }
                    };
                };

                editor = grid;
                break;
            }
            case ActionParameterKind.Color:
            {
                editor = ColorEditor(parameter, values, window, out subscribe);
                break;
            }
            case ActionParameterKind.Boolean:
            {
                var toggle = new ToggleSwitch
                {
                    Header = parameter.Title,
                    IsOn = string.Equals(values[parameter.Name], "true", StringComparison.OrdinalIgnoreCase)
                };
                subscribe = changed => toggle.Toggled += (_, _) =>
                {
                    values[parameter.Name] = toggle.IsOn ? "true" : "false";
                    changed();
                };
                editor = toggle;
                break;
            }
            case ActionParameterKind.Integer:
            {
                var number = new NumberBox
                {
                    Header = parameter.Title,
                    Minimum = parameter.Minimum ?? int.MinValue,
                    Maximum = parameter.Maximum ?? int.MaxValue,
                    SmallChange = 1,
                    LargeChange = 10,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    Value = double.TryParse(values[parameter.Name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0
                };
                subscribe = changed => number.ValueChanged += (_, args) =>
                {
                    if (!double.IsNaN(args.NewValue))
                        values[parameter.Name] = ((int)Math.Round(args.NewValue)).ToString(CultureInfo.InvariantCulture);
                    changed();
                };
                editor = number;
                break;
            }
            case ActionParameterKind.Choice:
            {
                var choices = parameter.Choices ?? Array.Empty<ActionParameterChoice>();
                var combo = new ComboBox
                {
                    Header = parameter.Title,
                    ItemsSource = choices.Select(c => c.Title).ToList(),
                    SelectedIndex = Math.Max(0, choices.ToList().FindIndex(c => string.Equals(c.Value, values[parameter.Name], StringComparison.OrdinalIgnoreCase))),
                    MinWidth = 220
                };
                subscribe = changed => combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedIndex >= 0) values[parameter.Name] = choices[combo.SelectedIndex].Value;
                    changed();
                };
                editor = combo;
                break;
            }
            case ActionParameterKind.Password:
            {
                var pwd = new PasswordBox
                {
                    Header = parameter.Title,
                    Password = values[parameter.Name],
                    PasswordRevealMode = PasswordRevealMode.Peek
                };
                subscribe = changed => pwd.PasswordChanged += (_, _) =>
                {
                    values[parameter.Name] = pwd.Password;
                    changed();
                };
                editor = pwd;
                break;
            }
            default:
            {
                var text = new TextBox
                {
                    Header = parameter.Title,
                    Text = values[parameter.Name],
                    PlaceholderText = parameter.Placeholder ?? string.Empty
                };
                subscribe = changed => text.TextChanged += (_, _) =>
                {
                    values[parameter.Name] = text.Text;
                    changed();
                };
                editor = text;
                break;
            }
        }

        if (!string.IsNullOrEmpty(parameter.Description))
            ToolTipService.SetToolTip(editor, parameter.Description);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(editor, parameter.Description ?? string.Empty);
        editor.Margin = new Thickness(0, 0, 0, 8);
        editor.Tag = new Tuple<Action<Action>, ActionParameterDefinition>(subscribe, parameter);
        return editor;
    }
}

/// <summary>The options page: the generated editors and preview, with "Cancel" and "Run" below them.</summary>
internal sealed class ActionOptionsView : HostedView
{
    private readonly Button _run;
    private readonly Action _onRun;

    public ActionOptionsView(string title, UIElement editors, Button run, Action onRun)
    {
        _run = run; _onRun = onRun;
        Title = title;
        PreferredSize = new Windows.Foundation.Size(640, 600);
        var root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 4, 24, 12) };
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("SubtitleTextBlockStyle", out var style) && style is Style subtitle)
            heading.Style = subtitle;
        root.Children.Add(heading);
        var scroll = new ScrollViewer { Content = editors, Padding = new Thickness(24, 0, 24, 12), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var footer = new Grid { Padding = new Thickness(24, 12, 24, 20), ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cancel = CreateCancelButton();
        Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        run.Click += (_, _) => onRun();
        Grid.SetColumn(run, 2); footer.Children.Add(run);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        // Like the former dialog: Enter runs (unless a control used it), Esc cancels.
        root.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; CancelToPalette(); }
            else if (args.Key == Windows.System.VirtualKey.Enter && _run.IsEnabled) { args.Handled = true; _onRun(); }
        };
        Content = root;
    }

    /// <summary>An unfinished choice should not be replaced by a fresh palette; the shortcut brings it back.</summary>
    public override bool KeepsWindow => true;
}
