namespace FileKit.Tabular;

public abstract record Cell;
public sealed record EmptyCell : Cell;
public sealed record TextCell(string Value) : Cell;
public sealed record NumberCell(double Value) : Cell;
public sealed record BooleanCell(bool Value) : Cell;
public sealed record DateCell(DateTime Value) : Cell;
public sealed record ErrorCell(string Code) : Cell;
public sealed record Table(IReadOnlyList<string>? Headers, IReadOnlyList<IReadOnlyList<Cell>> Rows);

public class TabularException : Exception
{
    public TabularException(string message) : base(message) { }
}

public sealed record CsvReadLimits(int MaxRows = 1_000_000, int MaxColumns = 16_384, long MaxBytes = 100L * 1024 * 1024, int MaxCells = 4_000_000);
