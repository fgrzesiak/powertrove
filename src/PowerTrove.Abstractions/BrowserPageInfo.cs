namespace PowerTrove.Abstractions;

/// <summary>
/// Transient browser signals explicitly sampled on PowerTrove invocation.
/// Not a browsing-history record; sensitive title and URL are redacted from diagnostics.
/// </summary>
public sealed record BrowserPageInfo(
    string Url, string Title, string Domain, bool HasSelection, int SelectionLength,
    bool IsArticle, int TableCount, int LargestTableRows, int LargestTableColumns,
    int ImageCount, int CodeBlockCount, string? Language, int WordCount)
{
    public BrowserTabHandle? Tab { get; init; }
    public override string ToString() => "BrowserPageInfo { Redacted }";
}
