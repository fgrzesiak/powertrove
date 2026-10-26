namespace FileKit.Xlsx;

public abstract record Cell;
public sealed record EmptyCell : Cell;
public sealed record TextCell(string Value) : Cell;
public sealed record NumberCell(double Value) : Cell;
public sealed record BooleanCell(bool Value) : Cell;
public sealed record DateCell(DateTime Value) : Cell;
public sealed record ErrorCell(string Code) : Cell;
public sealed record Worksheet(string Name, IReadOnlyList<IReadOnlyList<Cell>> Rows, bool HasHeader = false);
public sealed record Workbook(IReadOnlyList<Worksheet> Sheets, bool HasFormulas = false);
public sealed record XlsxLimits(long MaxPartBytes = 100L * 1024 * 1024, long MaxTotalBytes = 256L * 1024 * 1024,
    double MaxCompressionRatio = 1000, int MaxEntries = 10000, int MaxSheets = 256,
    int MaxRows = 1048576, int MaxColumns = 16384, int MaxCells = 4000000);
public sealed class XlsxException : Exception
{
    public XlsxException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class SheetNames
{
    public static string Unique(string proposed, ISet<string> used)
    {
        var name = new string(proposed.Select(c => c < ' ' || "[]:*?/\\".Contains(c) ? '_' : c).ToArray()).Trim().Trim('\'');
        if (name.Length == 0) name = "Sheet";
        name = Cut(name, 31);
        string candidate = name;
        for (int n = 2; used.Any(s => string.Equals(s, candidate, StringComparison.OrdinalIgnoreCase)); n++)
        {
            string suffix = $" ({n})";
            candidate = Cut(name, 31 - suffix.Length) + suffix;
        }
        used.Add(candidate);
        return candidate;
    }
    private static string Cut(string value, int length)
    {
        if (value.Length <= length) return value;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
}
