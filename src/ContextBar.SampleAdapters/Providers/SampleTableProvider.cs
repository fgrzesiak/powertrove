using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Core;

namespace ContextBar.SampleAdapters.Providers;

/// <summary>
/// A sample context provider that simulates capturing tabular data.
/// </summary>
public class SampleTableProvider : IContextProvider
{
    public string Id => "sample.table.provider";

    public ValueTask<bool> CanCaptureAsync(SourceWindowIdentity source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool canCapture = source.ProcessName?.Equals("excel.exe", StringComparison.OrdinalIgnoreCase) == true;
        return new ValueTask<bool>(canCapture);
    }

    public ValueTask<ContextSnapshot> CaptureAsync(Guid invocationId, SourceWindowIdentity source, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ValueTask<ContextSnapshot>(new ContextSnapshot(
                invocationId,
                source,
                ContextStatus.Canceled,
                new NoContent(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Id,
                "Canceled."
            ));
        }

        if (source.ProcessName?.Equals("excel.exe", StringComparison.OrdinalIgnoreCase) != true)
        {
            return new ValueTask<ContextSnapshot>(new ContextSnapshot(
                invocationId,
                source,
                ContextStatus.Unsupported,
                new NoContent(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Id,
                "Unsupported application."
            ));
        }

        var rows = new List<IReadOnlyList<CellValue>>
        {
            new List<CellValue> { new TextCell("Name"), new TextCell("Age"), new TextCell("Active") },
            new List<CellValue> { new TextCell("Alice"), new NumberCell(30), new BooleanCell(true) },
            new List<CellValue> { new TextCell("Bob"), new NumberCell(25), new BooleanCell(false) },
            new List<CellValue> { new EmptyCell(), new EmptyCell(), new ErrorCell("#DIV/0!") }
        };

        var content = new TableData(rows, "Microsoft Excel", "Sheet1!A1:C4");

        var snapshot = new ContextSnapshot(
            invocationId,
            source,
            ContextStatus.Available,
            content,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            Id,
            null
        );

        return new ValueTask<ContextSnapshot>(snapshot);
    }
}
