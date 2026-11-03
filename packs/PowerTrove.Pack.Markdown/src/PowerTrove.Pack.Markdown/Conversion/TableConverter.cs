using T = FileKit.Tabular;
using X = FileKit.Xlsx;

namespace PowerTrove.Pack.Markdown.Conversion;

/// <summary>
/// Excel workbooks to Markdown tables through FileKit.Xlsx and the same table writer as the Data pack's
/// CSV to Markdown (which covers CSV files). Each worksheet becomes a section; empty rows and columns at the edges are dropped
/// and the first used row is the header. Formulas are never evaluated: Excel's cached results are shown.
/// </summary>
internal static class TableConverter
{
    public static async Task<ConvertedMarkdown> ConvertXlsxAsync(Stream source, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, ct).ConfigureAwait(false);
        memory.Position = 0;
        X.Workbook workbook;
        try { workbook = await X.XlsxReader.ReadAsync(memory, cancellationToken: ct).ConfigureAwait(false); }
        catch (X.XlsxException) { throw new DocumentFormatException(SR.Get("ToMarkdownInvalid")); }
        var output = new MarkdownText();
        var sheets = workbook.Sheets.Select(s => (s.Name, Rows: Trim(s.Rows))).Where(s => s.Rows.Count > 0).ToArray();
        foreach (var (name, rows) in sheets)
        {
            ct.ThrowIfCancellationRequested();
            if (sheets.Length > 1) output.Block("## " + MarkdownText.Inline(name));
            var table = new T.Table(rows[0].Select(Text).ToArray(), rows.Skip(1).Select(r => (IReadOnlyList<T.Cell>)r.Select(Cell).ToArray()).ToArray());
            output.Block(Write(table, ct));
        }
        if (output.IsEmpty) throw new DocumentFormatException(SR.Get("ToMarkdownNoContent"));
        return output.Build();
    }

    private static string Write(T.Table table, CancellationToken ct)
    {
        using var writer = new StringWriter();
        T.MarkdownTableWriter.Write(writer, table, ct);
        return writer.ToString();
    }

    /// <summary>Drops empty rows and the empty columns to the left and right of the used range.</summary>
    private static IReadOnlyList<IReadOnlyList<X.Cell>> Trim(IReadOnlyList<IReadOnlyList<X.Cell>> rows)
    {
        var used = rows.Where(r => r.Any(c => !IsEmpty(c))).ToArray();
        if (used.Length == 0) return [];
        int first = used.Min(r => Enumerable.Range(0, r.Count).First(i => !IsEmpty(r[i])));
        int last = used.Max(r => Enumerable.Range(0, r.Count).Last(i => !IsEmpty(r[i])));
        return used.Select(r => (IReadOnlyList<X.Cell>)Enumerable.Range(first, last - first + 1).Select(i => i < r.Count ? r[i] : new X.EmptyCell()).ToArray()).ToArray();
    }

    private static bool IsEmpty(X.Cell cell) => cell is X.EmptyCell || cell is X.TextCell { Value: var v } && string.IsNullOrWhiteSpace(v);

    private static T.Cell Cell(X.Cell cell) => cell switch
    {
        X.TextCell text => new T.TextCell(text.Value),
        X.NumberCell number => new T.NumberCell(number.Value),
        X.BooleanCell boolean => new T.BooleanCell(boolean.Value),
        X.DateCell date => new T.DateCell(date.Value),
        X.ErrorCell error => new T.ErrorCell(error.Code),
        _ => new T.EmptyCell()
    };

    private static string Text(X.Cell cell) => T.CsvWriter.FormatCellValue(Cell(cell));
}
