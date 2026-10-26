namespace PowerTrove.Pack.Tables;

internal static class TableMapping
{
    public static FileKit.Tabular.Table Map(TableData table, bool firstRowAsHeader = false)
    {
        var rows = table.Rows.Select(Map).ToArray();
        return firstRowAsHeader && rows.Length > 0
            ? new(FileKit.Tabular.JsonTableWriter.ResolveHeaders(rows[0]), rows.Skip(1).ToArray())
            : new(null, rows);
    }
    public static IReadOnlyList<FileKit.Tabular.Cell> Map(IReadOnlyList<CellValue> row) => row.Select(Map).ToArray();
    public static FileKit.Tabular.Cell Map(CellValue cell) => cell switch
    {
        TextCell t => new FileKit.Tabular.TextCell(t.Value),
        NumberCell n => new FileKit.Tabular.NumberCell(n.Value),
        BooleanCell b => new FileKit.Tabular.BooleanCell(b.Value),
        DateCell d => new FileKit.Tabular.DateCell(d.Value),
        ErrorCell e => new FileKit.Tabular.ErrorCell(e.Code),
        _ => new FileKit.Tabular.EmptyCell()
    };
}
