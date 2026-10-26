using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FileKit.Tabular;

public static partial class JsonTableWriter
{
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

    public static void WriteCellValue(Utf8JsonWriter writer, Cell cell)
    {
        switch (cell)
        {
            case NumberCell n:
                writer.WriteNumberValue(n.Value);
                break;
            case BooleanCell b:
                writer.WriteBooleanValue(b.Value);
                break;
            case EmptyCell:
                writer.WriteNullValue();
                break;
            case DateCell d:
                writer.WriteStringValue(d.Value.ToString("s", System.Globalization.CultureInfo.InvariantCulture));
                break;
            case ErrorCell e:
                writer.WriteStringValue(e.Code ?? "");
                break;
            case TextCell t:
                writer.WriteStringValue(t.Value ?? "");
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
