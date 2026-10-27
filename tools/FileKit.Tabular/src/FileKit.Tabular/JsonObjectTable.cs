using System.Text.Json;

namespace FileKit.Tabular;

/// <summary>Typed projection of an array of objects. Dates stay text; numbers that cannot
/// survive Excel's 15 significant digits stay text. Nested containers default to JSON text.</summary>
public static class JsonObjectTable
{
    public static Table Project(JsonElement root, bool flatten = false, IReadOnlyList<string>? columns = null,
        CancellationToken cancellationToken = default)
    {
        var headers = DiscoverColumns(root, flatten, cancellationToken).ToList();
        if (columns is not null)
        {
            var available = headers.ToHashSet(StringComparer.Ordinal);
            if (columns.Count == 0 || columns.Distinct(StringComparer.Ordinal).Count() != columns.Count || columns.Any(c => !available.Contains(c)))
                throw new TabularException("Columns must be unique existing names.");
            headers = columns.ToList();
        }
        if (headers.Count == 0 || (root.GetArrayLength() + 1L) * headers.Count > 4_000_000)
            throw new TabularException("Empty table or cell limit exceeded.");
        var rows = new List<IReadOnlyList<Cell>>(root.GetArrayLength());
        foreach (var row in root.EnumerateArray())
        {
            var fields = Fields(row, flatten, cancellationToken).ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);
            rows.Add(headers.Select(h => fields.TryGetValue(h, out var value) ? Cell(value) : new EmptyCell()).ToArray());
        }
        return new(headers, rows);
    }

    /// <summary>Returns ordered field names without materializing cell values.</summary>
    public static IReadOnlyList<string> DiscoverColumns(JsonElement root, bool flatten = false, CancellationToken cancellationToken = default)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is < 1 or > 1_000_000)
            throw new TabularException("Expected a nonempty array of objects within the row limit.");
        var headers = new List<string>();
        var origins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in root.EnumerateArray())
        {
            foreach (var (name, origin, _) in Fields(row, flatten, cancellationToken))
            {
                if (origins.TryGetValue(name, out string? previous))
                {
                    if (previous != origin) throw new TabularException("Flattened column names collide.");
                }
                else
                {
                    if (name.Length is < 1 or > 32767 || headers.Count >= 16384) throw new TabularException("Invalid column name or column limit exceeded.");
                    origins.Add(name, origin); headers.Add(name);
                }
            }
        }
        return headers.AsReadOnly();
    }

    private static IEnumerable<(string Name, string Origin, JsonElement Value)> Fields(JsonElement row, bool flatten, CancellationToken ct)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new TabularException("Every row must be an object.");
        return Visit(row, "", "", flatten, ct);
    }
    private static IEnumerable<(string Name, string Origin, JsonElement Value)> Visit(JsonElement obj, string prefix, string pointer, bool flatten, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested();
            if (!names.Add(property.Name)) throw new TabularException("Duplicate JSON property.");
            string name = prefix + property.Name;
            string origin = pointer + "/" + property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
            if (flatten && property.Value.ValueKind == JsonValueKind.Object && property.Value.EnumerateObject().Any())
            {
                foreach (var field in Visit(property.Value, name + ".", origin, true, ct)) yield return field;
            }
            else yield return (name, origin, property.Value);
        }
    }
    private static Cell Cell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => new EmptyCell(),
        JsonValueKind.True => new BooleanCell(true),
        JsonValueKind.False => new BooleanCell(false),
        JsonValueKind.Number when IsExcelNumber(value) => new NumberCell(value.GetDouble()),
        JsonValueKind.String => new TextCell(value.GetString()!),
        _ => new TextCell(value.GetRawText())
    };
    private static bool IsExcelNumber(JsonElement value)
    {
        if (!value.TryGetDecimal(out decimal bounded) || !value.TryGetDouble(out double floating) || !double.IsFinite(floating) || bounded == 0 && floating != 0) return false;
        // Compare exact decimal spellings. Decimal.TryParse itself rounds long mantissas
        // and underflows tiny values, which would silently corrupt identifiers/measurements.
        string raw = value.GetRawText();
        return raw.Length <= 1024 && Canonical(raw) == Canonical(floating.ToString("G15", System.Globalization.CultureInfo.InvariantCulture));
    }
    private static string? Canonical(string text)
    {
        int e = text.IndexOfAny(['e', 'E']); long exponent = 0;
        if (e >= 0 && !long.TryParse(text.AsSpan(e + 1), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out exponent)) return null;
        string mantissa = e >= 0 ? text[..e] : text;
        bool negative = mantissa.StartsWith('-'); if (negative) mantissa = mantissa[1..];
        int dot = mantissa.IndexOf('.'); int fractional = dot < 0 ? 0 : mantissa.Length - dot - 1;
        string digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return "0";
        string significant = digits.TrimEnd('0');
        try { exponent = checked(exponent - fractional + digits.Length - significant.Length); }
        catch (OverflowException) { return null; }
        return (negative ? "-" : "") + significant + "e" + exponent.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
