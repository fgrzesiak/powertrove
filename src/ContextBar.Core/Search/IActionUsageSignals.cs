namespace ContextBar.Core.Search;

public interface IActionUsageSignals
{
    double SelectionScore(string signatureKey, string actionId);
    string? LastUsedAction(string signatureKey);
    void Refresh();
}

public enum ActionHighlight
{
    None,
    LastUsedForSelection,
    Favorite,
    BestFit
}
