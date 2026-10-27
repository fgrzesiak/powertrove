using System.Text.Json;

namespace PowerTrove.Application.ViewModels;

/// <summary>Input-derived labels stay in this dialog state, never in history or diagnostics.</summary>
public sealed class MultiSelectParameterState
{
    private List<string> _order = [];
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private IReadOnlyList<ActionParameterChoice> _choices = [];
    public bool UseAll { get; private set; }
    public bool SelectionAdjusted { get; private set; }
    public IReadOnlyList<ActionParameterChoice> Choices => _order.Join(_choices, v => v, c => c.Value, (_, c) => c, StringComparer.Ordinal).ToArray();
    public string Value => UseAll ? string.Empty : JsonSerializer.Serialize(_order.Where(_selected.Contains));
    public bool IsWithinLimit => Value.Length <= ActionParameterChoiceSet.MaximumSelectionLength;

    public MultiSelectParameterState(string value)
    {
        UseAll = string.IsNullOrWhiteSpace(value);
        if (UseAll) return;
        try
        {
            if (value.Length > ActionParameterChoiceSet.MaximumSelectionLength) throw new JsonException();
            using var doc = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 2 });
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()) || !_selected.Add(item.GetString()!)) throw new JsonException();
                _order.Add(item.GetString()!);
            }
        }
        catch (JsonException) { _order.Clear(); _selected.Clear(); SelectionAdjusted = true; }
    }

    public bool UpdateChoices(IReadOnlyList<ActionParameterChoice> choices)
    {
        if (choices.Count > ActionParameterChoiceSet.MaximumChoices || choices.Any(c => c is null ||
                string.IsNullOrWhiteSpace(c.Value) || string.IsNullOrWhiteSpace(c.Title) ||
                c.Value.Length > ActionParameterChoiceSet.MaximumChoiceLength || c.Title.Length > ActionParameterChoiceSet.MaximumChoiceLength ||
                c.Value.Contains('\0') || c.Title.Contains('\0')) || choices.Select(c => c.Value).Distinct(StringComparer.Ordinal).Count() != choices.Count)
            throw new ArgumentException("Invalid parameter choices.", nameof(choices));
        string before = Value;
        var available = choices.Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        if (_selected.RemoveWhere(v => !available.Contains(v)) > 0 && !UseAll) SelectionAdjusted = true;
        _order = _order.Where(available.Contains).Concat(choices.Select(c => c.Value).Except(_order, StringComparer.Ordinal)).ToList();
        _choices = choices.ToArray();
        if (UseAll) { _order = choices.Select(c => c.Value).ToList(); _selected.Clear(); _selected.UnionWith(available); }
        return before != Value;
    }

    public bool IsSelected(string value) => UseAll || _selected.Contains(value);
    public void SetUseAll(bool useAll)
    {
        UseAll = useAll; SelectionAdjusted = false;
        if (useAll) { _order = _choices.Select(c => c.Value).ToList(); _selected.Clear(); _selected.UnionWith(_order); }
    }
    public void SetSelected(string value, bool selected)
    {
        if (!_order.Contains(value, StringComparer.Ordinal)) throw new ArgumentException("Unknown parameter choice.", nameof(value));
        if (UseAll) { _selected.Clear(); _selected.UnionWith(_order); UseAll = false; }
        if (selected) _selected.Add(value); else _selected.Remove(value);
        SelectionAdjusted = false;
    }
    public void Clear() { UseAll = false; _selected.Clear(); SelectionAdjusted = false; }
    public void Move(string value, int direction)
    {
        int index = _order.IndexOf(value), next = index + direction;
        if (direction is not (-1 or 1) || index < 0 || next < 0 || next >= _order.Count) return;
        UseAll = false;
        (_order[index], _order[next]) = (_order[next], _order[index]);
    }
    public override string ToString() => "MultiSelectParameterState { Payload = redacted }";
}
