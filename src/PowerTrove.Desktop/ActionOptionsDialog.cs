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

public sealed record ActionOptionsResult(DictionaryParameters Parameters, bool Remember);

/// <summary>
/// Generic options and confirmation dialog generated from an action's declared
/// <see cref="ActionParameterDefinition"/>s. Shows the live preview (counts and
/// warnings only; rename names are confined to this dialog) and disables "Run" while the action
/// reports a blocked preview.
/// </summary>
public static class ActionOptionsDialog
{
    public static async Task<ActionOptionsResult?> ShowAsync(
        Window window,
        ActionDescriptor descriptor,
        IReadOnlyDictionary<string, string>? savedValues,
        Func<ActionParameters, CancellationToken, Task<ActionPreview?>> preview,
        Action? openVisualEditor = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in descriptor.Parameters ?? Array.Empty<ActionParameterDefinition>())
        {
            values[parameter.Name] = descriptor.CanRememberParameters && savedValues != null && savedValues.TryGetValue(parameter.Name, out var saved)
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
        bool openVisual = false;
        ContentDialog? dialogRef = null;
        if (openVisualEditor != null)
        {
            // Second, visual way to do the same: pages as thumbnails instead of typed numbers.
            var visual = new HyperlinkButton
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { new FontIcon { Glyph = "\uE8A9", FontSize = 14 }, new TextBlock { Text = UiText.OrganizerOpenVisual } }
                },
                Margin = new Thickness(-10, 0, 0, 8)
            };
            visual.Click += (_, _) => { openVisual = true; dialogRef?.Hide(); };
            panel.Children.Add(visual);
        }
        foreach (var parameter in descriptor.Parameters ?? Array.Empty<ActionParameterDefinition>())
            panel.Children.Add(CreateEditor(parameter, values, window));
        panel.Children.Add(previewText);
        panel.Children.Add(warningsText);
        panel.Children.Add(renameTable);
        panel.Children.Add(consent);
        if (descriptor.CanRememberParameters && descriptor.Parameters is { Count: > 0 }) panel.Children.Add(remember);

        var dialog = new ContentDialog
        {
            XamlRoot = window.Content.XamlRoot,
            Title = descriptor.Title,
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            PrimaryButtonText = UiText.OptionsRun,
            CloseButtonText = UiText.CommonCancel,
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        dialogRef = dialog;

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
            dialog.IsPrimaryButtonEnabled = false;
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

                string outputs = result.ExpectedOutputs.Count switch
                {
                    0 => string.Empty,
                    1 => " " + UiText.OptionsOneOutput,
                    var n => " " + UiText.Format(UiText.OptionsManyOutputs, n)
                };
                previewText.Text = (result.PreviewText ?? string.Empty) + (result.IsBlocked ? string.Empty : outputs);
                warningsText.Text = string.Join(Environment.NewLine, result.Warnings);
                if (result.RenameRows is { Count: > 0 } rows && descriptor.Id == "contextbar.files.rename")
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
                dialog.IsPrimaryButtonEnabled = validPreview && confirmation is null;
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!cts.IsCancellationRequested)
                {
                    previewText.Text = UiText.OptionsSelectionGone;
                    warningsText.Text = string.Empty;
                    renameTable.Children.Clear();
                    dialog.IsPrimaryButtonEnabled = false;
                }
            }
        }

        consent.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = validPreview;
        consent.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = validPreview && confirmation is null;

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
                            var dependentParam = descriptor.Parameters.FirstOrDefault(dp => dp.Name == p.VisibleWhenParameter);
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
        var outcome = await dialog.ShowAsync();
        refresh?.Cancel();
        renameTable.Children.Clear();

        if (openVisual)
        {
            openVisualEditor!();
            return null;
        }

        if (outcome != ContentDialogResult.Primary)
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

    private static FrameworkElement CreateEditor(ActionParameterDefinition parameter, Dictionary<string, string> values, Window window)
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
                var panel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(0, 8, 0, 8)
                };

                var label = new TextBlock
                {
                    Text = parameter.Title,
                    Margin = new Thickness(0, 0, 0, 4)
                };

                string currentHex = values.TryGetValue(parameter.Name, out var v) && !string.IsNullOrWhiteSpace(v)
                    ? v
                    : (parameter.DefaultValue ?? "#FFFFFF");

                var colorPicker = new ColorPicker
                {
                    ColorSpectrumShape = ColorSpectrumShape.Box,
                    IsMoreButtonVisible = false,
                    IsColorSliderVisible = true,
                    IsColorChannelTextInputVisible = false,
                    IsHexInputVisible = true,
                    IsAlphaEnabled = false,
                    IsAlphaSliderVisible = false,
                    IsAlphaTextInputVisible = false
                };

                if (Microsoft.UI.Colors.Transparent.ToString() != currentHex)
                {
                    string h = currentHex.StartsWith("#") ? currentHex.Substring(1) : currentHex;
                    if (h.Length == 6 &&
                        byte.TryParse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
                        byte.TryParse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
                        byte.TryParse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    {
                        colorPicker.Color = Windows.UI.Color.FromArgb(255, r, g, b);
                    }
                }

                var flyout = new Flyout
                {
                    Content = colorPicker
                };

                var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = 24,
                    Height = 24,
                    Fill = new SolidColorBrush(colorPicker.Color),
                    Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                    StrokeThickness = 1
                };

                var button = new Button
                {
                    Content = rect,
                    Flyout = flyout
                };

                panel.Children.Add(label);
                panel.Children.Add(button);

                subscribe = changed =>
                {
                    colorPicker.ColorChanged += (_, args) =>
                    {
                        var c = args.NewColor;
                        rect.Fill = new SolidColorBrush(c);
                        values[parameter.Name] = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                        changed();
                    };
                };

                editor = panel;
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
