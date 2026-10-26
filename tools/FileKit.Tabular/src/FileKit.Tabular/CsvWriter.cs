using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FileKit.Tabular;

public static partial class CsvWriter
{
    public static string FormatCellValue(Cell cell)
    {
        return cell switch
        {
            EmptyCell => "",
            TextCell t => t.Value ?? "",
            NumberCell n => n.Value.ToString(CultureInfo.InvariantCulture),
            BooleanCell b => b.Value.ToString(CultureInfo.InvariantCulture),
            DateCell d => d.Value.ToString("s", CultureInfo.InvariantCulture),
            ErrorCell e => e.Code ?? "",
            _ => ""
        };
    }

    public static string EscapeCsvField(string field, string delimiter)
    {
        bool needsQuotes = field.Contains(delimiter) || field.Contains("\"") || field.Contains("\n") || field.Contains("\r");

        if (needsQuotes)
        {
            string escaped = field.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }
        return field;
    }
}
