namespace PowerTrove.Abstractions;

public enum BrowserPageOperation
{
    ArticleMarkdown, PageMarkdown, Tables, Links, Images,
    CaptureVisiblePng, CaptureFullPng, CaptureMhtml
}

public sealed record BrowserLink(string Title, string Url)
{
    public override string ToString() => "BrowserLink { Redacted }";
}
public sealed record BrowserImage(string Url, string Alt, int Width, int Height)
{
    public override string ToString() => "BrowserImage { Redacted }";
}
public sealed record BrowserTable(IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public override string ToString() => $"BrowserTable {{ Rows = {Rows.Count}, Redacted }}";

    public TableData ToTableData() => new(Rows.Select(row => (IReadOnlyList<CellValue>)row.Select(value =>
        (CellValue)(string.IsNullOrEmpty(value) ? new EmptyCell() : new TextCell(value))).ToArray()).ToArray(),
        "Browser", null);
}

public sealed record BrowserPageContent(
    BrowserPageOperation Operation,
    string? Markdown = null,
    IReadOnlyList<BrowserTable>? Tables = null,
    IReadOnlyList<BrowserLink>? Links = null,
    IReadOnlyList<BrowserImage>? Images = null,
    byte[]? Binary = null)
{
    public override string ToString() => "BrowserPageContent { Redacted }";
}

/// <summary>On-demand only. Host verifies a live, previously captured tab handle.</summary>
public interface IBrowserPageService
{
    Task<BrowserPageContent?> FetchAsync(BrowserPageInfo page,
        BrowserPageOperation operation, CancellationToken cancellationToken);
}
