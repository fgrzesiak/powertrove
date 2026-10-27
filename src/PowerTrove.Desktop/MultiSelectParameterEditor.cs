using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PowerTrove.Application.ViewModels;

namespace PowerTrove.Desktop;

/// <summary>A reusable checkbox editor for choices supplied by an action preview.</summary>
internal sealed class MultiSelectParameterEditor
{
    private readonly ActionParameterDefinition _parameter;
    private readonly Dictionary<string, string> _values;
    private readonly MultiSelectParameterState _state;
    private readonly CheckBox _all = new() { Content = UiText.OptionsSelectAll };
    private readonly Button _none = new() { Content = UiText.OptionsSelectNone };
    private readonly StackPanel _items = new() { Spacing = 4 };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _adjustment = new() { Text = UiText.OptionsSelectionAdjusted, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, CheckBox> _checks = new(StringComparer.Ordinal);
    private readonly List<(Button Button, string Value, int Direction)> _moves = [];
    private Action? _changed;
    private bool _updating;
    private string? _context;
    public StackPanel Root { get; } = new() { Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
    public bool IsWithinLimit => _state.IsWithinLimit;

    public MultiSelectParameterEditor(ActionParameterDefinition parameter, Dictionary<string, string> values)
    {
        _parameter = parameter; _values = values;
        _state = new(values[parameter.Name]); values[parameter.Name] = _state.Value;
        Root.Children.Add(new TextBlock { Text = parameter.Title, TextWrapping = TextWrapping.Wrap });
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        commands.Children.Add(_all); commands.Children.Add(_none); Root.Children.Add(commands);
        Root.Children.Add(_description);
        Root.Children.Add(new ScrollViewer { Content = _items, MaxHeight = 210, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Root.Children.Add(_adjustment);
        Root.Tag = new Tuple<Action<Action>, ActionParameterDefinition>(callback => _changed = callback, parameter);
        AutomationProperties.SetName(Root, parameter.Title);
        _description.Text = parameter.Description ?? string.Empty;
        _all.Checked += (_, _) => { if (!_updating) { _state.SetUseAll(true); Rebuild(); Changed(); } };
        _all.Unchecked += (_, _) => { if (!_updating) { _state.SetUseAll(false); Changed(); } };
        _none.Click += (_, _) => { _state.Clear(); Changed(); };
        Synchronize();
    }

    public bool ApplyChoices(ActionParameterChoiceSet? set)
    {
        var choices = set?.Choices ?? Array.Empty<ActionParameterChoice>();
        var before = _state.Choices.ToArray();
        bool changed = _state.UpdateChoices(choices);
        bool rebuild = _context != set?.ContextKey || !before.SequenceEqual(_state.Choices);
        _context = set?.ContextKey;
        _description.Text = set?.Description ?? _parameter.Description ?? string.Empty;
        _values[_parameter.Name] = _state.Value;
        if (rebuild) Rebuild(); else Synchronize();
        return changed;
    }

    private void Rebuild()
    {
        _updating = true; _checks.Clear(); _moves.Clear(); _items.Children.Clear();
        foreach (var choice in _state.Choices)
        {
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new CheckBox { Content = new TextBlock { Text = choice.Title, TextWrapping = TextWrapping.Wrap }, IsChecked = _state.IsSelected(choice.Value) };
            AutomationProperties.SetName(check, choice.Title);
            check.Checked += (_, _) => { if (!_updating) { _state.SetSelected(choice.Value, true); Changed(); } };
            check.Unchecked += (_, _) => { if (!_updating) { _state.SetSelected(choice.Value, false); Changed(); } };
            _checks.Add(choice.Value, check); row.Children.Add(check);
            foreach (int direction in new[] { -1, 1 })
            {
                string label = direction < 0 ? UiText.OptionsMoveUp : UiText.OptionsMoveDown;
                var button = new Button { Content = new FontIcon { Glyph = direction < 0 ? "\uE70E" : "\uE70D", FontSize = 12 }, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
                button.Click += (_, _) => { _state.Move(choice.Value, direction); Rebuild(); Changed(); };
                Grid.SetColumn(button, direction < 0 ? 1 : 2); row.Children.Add(button); _moves.Add((button, choice.Value, direction));
            }
            _items.Children.Add(row);
        }
        _updating = false; Synchronize();
    }

    private void Changed()
    {
        _values[_parameter.Name] = _state.Value; Synchronize(); _changed?.Invoke();
    }
    private void Synchronize()
    {
        _updating = true; _all.IsChecked = _state.UseAll;
        _none.IsEnabled = _state.Choices.Count > 0;
        foreach (var (value, check) in _checks) check.IsChecked = _state.IsSelected(value);
        var order = _state.Choices.Select(c => c.Value).ToList();
        foreach (var (button, value, direction) in _moves)
        { int next = order.IndexOf(value) + direction; button.IsEnabled = next >= 0 && next < order.Count; }
        _adjustment.Visibility = _state.SelectionAdjusted ? Visibility.Visible : Visibility.Collapsed;
        _updating = false;
    }
}
