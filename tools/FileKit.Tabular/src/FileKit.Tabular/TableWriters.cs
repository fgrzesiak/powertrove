using System.Text;
using System.Text.Json;

namespace FileKit.Tabular;

public static partial class CsvWriter
{
    public static void Write(TextWriter writer, Table table, string delimiter = ",", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(delimiter)) throw new ArgumentException("Delimiter is required.", nameof(delimiter));
        if (table.Headers is not null) WriteRow(table.Headers);
        foreach (var row in table.Rows) { cancellationToken.ThrowIfCancellationRequested(); WriteRow(row.Select(FormatCellValue)); }
        void WriteRow(IEnumerable<string> cells) { writer.Write(string.Join(delimiter, cells.Select(c => EscapeCsvField(c, delimiter)))); writer.Write("\r\n"); }
    }
}

public static partial class JsonTableWriter
{
    public static void Write(Stream output, Table table, bool indented = true, CancellationToken cancellationToken = default)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = indented });
        Write(writer, table, cancellationToken);
    }
    public static void Write(Utf8JsonWriter writer, Table table, CancellationToken cancellationToken = default)
    {
        writer.WriteStartArray();
        foreach (var row in table.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (table.Headers is null) { writer.WriteStartArray(); foreach (var cell in row) WriteCellValue(writer, cell); writer.WriteEndArray(); }
            else
            {
                writer.WriteStartObject();
                for (int c = 0; c < table.Headers.Count; c++) { writer.WritePropertyName(table.Headers[c]); WriteCellValue(writer, c < row.Count ? row[c] : new EmptyCell()); }
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
    }
}

public static partial class MarkdownTableWriter
{
    public static void Write(TextWriter output, Table table, CancellationToken cancellationToken = default)
    {
        IEnumerable<IReadOnlyList<Cell>> rows = table.Headers is null ? table.Rows : new[] { (IReadOnlyList<Cell>)table.Headers.Select(h => (Cell)new TextCell(h)).ToArray() }.Concat(table.Rows);
        bool first = true;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write("| " + string.Join(" | ", row.Select(CsvWriter.FormatCellValue).Select(EscapeMarkdownField)) + " |\n");
            if (first) { output.Write("| " + string.Join(" | ", row.Select(_ => "---")) + " |\n"); first = false; }
        }
    }
}

public static partial class SqlInsertWriter
{
    public static void Write(TextWriter output, Table table, string tableName = "ExportedData", string dialect = "PostgreSQL", CancellationToken cancellationToken = default)
    {
        NormalizeDialect(dialect); QuoteIdentifier(tableName, dialect);
        foreach (var row in table.Rows) { cancellationToken.ThrowIfCancellationRequested(); output.WriteLine(BuildInsertStatement(tableName, dialect, row, table.Headers?.ToArray())); }
    }
}
