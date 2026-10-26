using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FileKit.Tabular;

public static partial class SqlInsertWriter
{
    public static string NormalizeDialect(string dialect) =>
        dialect.Trim().ToLowerInvariant() switch
        {
            "postgresql" => "postgresql",
            "mysql" => "mysql",
            "sqlite" => "sqlite",
            "sqlserver" => "sqlserver",
            _ => throw new ArgumentException("Unsupported SQL dialect.", nameof(dialect))
        };

    public static string QuoteIdentifier(string identifier, string dialect)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("SQL identifier must not be empty.", nameof(identifier));

        return NormalizeDialect(dialect) switch
        {
            "mysql" => "`" + identifier.Replace("`", "``") + "`",
            "sqlserver" => "[" + identifier.Replace("]", "]]") + "]",
            _ => "\"" + identifier.Replace("\"", "\"\"") + "\""
        };
    }

    public static string FormatValue(Cell cell, string dialect)
    {
        switch (cell)
        {
            case EmptyCell:
            case ErrorCell:
                return "NULL";

            case NumberCell n:
                return n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

            case BooleanCell b:
                return NormalizeDialect(dialect) == "postgresql"
                    ? (b.Value ? "TRUE" : "FALSE")
                    : (b.Value ? "1" : "0");

            case DateCell d:
                var dateStr = d.Value.ToString("s", System.Globalization.CultureInfo.InvariantCulture);
                return dialect.ToLowerInvariant() == "sqlserver" ? $"N'{dateStr}'" : $"'{dateStr}'";

            case TextCell t:
                var textValue = t.Value ?? "";
                var escapedText = textValue.Replace("'", "''");
                return dialect.ToLowerInvariant() == "sqlserver" ? $"N'{escapedText}'" : $"'{escapedText}'";

            default:
                return "NULL";
        }
    }

    public static string[] ResolveHeaders(IReadOnlyList<Cell> headerRow)
    {
        var headers = new string[headerRow.Count];
        var seenHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < headerRow.Count; i++)
        {
            var cell = headerRow[i];
            string baseHeader = cell switch
            {
                TextCell t when !string.IsNullOrWhiteSpace(t.Value) => t.Value,
                NumberCell n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                BooleanCell b => b.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DateCell d => d.Value.ToString("s", System.Globalization.CultureInfo.InvariantCulture),
                ErrorCell e when !string.IsNullOrWhiteSpace(e.Code) => e.Code,
                _ => $"Column_{i + 1}"
            };

            if (string.IsNullOrWhiteSpace(baseHeader))
            {
                baseHeader = $"Column_{i + 1}";
            }

            string finalHeader = baseHeader;
            int counter = 1;
            while (seenHeaders.Contains(finalHeader))
            {
                finalHeader = $"{baseHeader}_{counter}";
                counter++;
            }

            seenHeaders.Add(finalHeader);
            headers[i] = finalHeader;
        }

        return headers;
    }

    public static string BuildInsertStatement(
        string tableName,
        string dialect,
        IReadOnlyList<Cell> row,
        string[]? headers = null)
    {
        var quotedTable = QuoteIdentifier(tableName, dialect);
        var columns = string.Empty;

        if (headers != null && headers.Length > 0)
        {
            var quotedHeaders = new string[headers.Length];
            for (int i = 0; i < headers.Length; i++)
            {
                quotedHeaders[i] = QuoteIdentifier(headers[i], dialect);
            }
            columns = $" ({string.Join(", ", quotedHeaders)})";
        }

        var values = new string[row.Count];
        for (int i = 0; i < row.Count; i++)
        {
            values[i] = FormatValue(row[i], dialect);
        }

        return $"INSERT INTO {quotedTable}{columns} VALUES ({string.Join(", ", values)});";
    }
}
