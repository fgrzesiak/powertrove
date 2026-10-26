using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Core;

namespace PowerTrove.Platform.Windows.Excel;

public sealed class ExcelSelectionProvider : IContextProvider
{
    private readonly IExcelInterop _interop;
    public string Id => "powertrove.provider.excel";

    public ExcelSelectionProvider() : this(new ExcelInterop())
    {
    }

    public ExcelSelectionProvider(IExcelInterop interop)
    {
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
    }

    public ValueTask<bool> CanCaptureAsync(SourceWindowIdentity source, CancellationToken cancellationToken)
    {
        bool isExcelProcess = string.Equals(source.ProcessName, "excel", StringComparison.OrdinalIgnoreCase);
        return new ValueTask<bool>(isExcelProcess);
    }

    // A blocked COM server holds this slot until it returns. Never spawn unbounded abandoned workers.
    private static readonly SemaphoreSlim CaptureSlot = new(1, 1);
    private readonly TimeSpan _captureTimeout = TimeSpan.FromSeconds(2);

    public ExcelSelectionProvider(IExcelInterop interop, TimeSpan captureTimeout) : this(interop)
    {
        if (captureTimeout <= TimeSpan.Zero || captureTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(captureTimeout));
        _captureTimeout = captureTimeout;
    }

    public async ValueTask<ContextSnapshot> CaptureAsync(Guid invocationId, SourceWindowIdentity source, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return CreateSnapshot(invocationId, source, ContextStatus.Canceled, new NoContent());
        if (!CaptureSlot.Wait(0))
            return CreateSnapshot(invocationId, source, ContextStatus.TimedOut, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExcelCaptureBusy);

        using var deadline = new CancellationTokenSource(_captureTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        var completion = new TaskCompletionSource<ContextSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var worker = new Thread(() =>
            {
                ContextSnapshot result;
                try { result = CaptureOnWorker(invocationId, source, token); }
                finally { CaptureSlot.Release(); }
                completion.TrySetResult(result);
            }) { IsBackground = true, Name = "PowerTrove Excel capture" };
            if (OperatingSystem.IsWindows()) worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
        catch (Exception)
        {
            CaptureSlot.Release();
            return CreateSnapshot(invocationId, source, ContextStatus.Failed, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExcelCaptureStartFailed);
        }

        try
        {
            var result = await completion.Task.WaitAsync(token).ConfigureAwait(false);
            // A server may ignore cancellation. Never publish its late result.
            if (cancellationToken.IsCancellationRequested)
                return CreateSnapshot(invocationId, source, ContextStatus.Canceled, new NoContent());
            if (deadline.IsCancellationRequested)
                return CreateSnapshot(invocationId, source, ContextStatus.TimedOut, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExcelCaptureTimedOut);
            return result;
        }
        catch (OperationCanceledException)
        {
            return CreateSnapshot(invocationId, source,
                cancellationToken.IsCancellationRequested ? ContextStatus.Canceled : ContextStatus.TimedOut,
                new NoContent(), cancellationToken.IsCancellationRequested ? null : PowerTrove.Platform.Windows.Resources.Strings.ExcelCaptureTimedOut);
        }
    }

    private ContextSnapshot CaptureOnWorker(Guid invocationId, SourceWindowIdentity source, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var capture = _interop.Capture(source, token);
            token.ThrowIfCancellationRequested();
            if (capture.Status != ContextStatus.Available)
                return CreateSnapshot(invocationId, source, capture.Status, new NoContent(), capture.Message);
            var selectionInfo = capture.Selection;
            if (selectionInfo == null)
                return CreateSnapshot(invocationId, source, ContextStatus.Failed, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExcelCaptureNoData);
            if (!selectionInfo.IsRange)
                return CreateSnapshot(invocationId, source, ContextStatus.Unsupported, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExcelItemNotRange);
            if (selectionInfo.AreaCount != 1)
                return CreateSnapshot(invocationId, source, ContextStatus.Unsupported, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExcelDisjointSelection);
            if (selectionInfo.RowCount <= 0 || selectionInfo.ColumnCount <= 0 ||
                (long)selectionInfo.RowCount * selectionInfo.ColumnCount > ExcelInterop.MaximumCells)
                return CreateSnapshot(invocationId, source, ContextStatus.Unsupported, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExcelRangeTooLarge);

            var values = selectionInfo.Values;
            if (values != null && (values.GetLength(0) != selectionInfo.RowCount || values.GetLength(1) != selectionInfo.ColumnCount))
                return CreateSnapshot(invocationId, source, ContextStatus.Failed, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExcelInconsistentDimensions);
            int rowBound = values?.GetLowerBound(0) ?? 0;
            int columnBound = values?.GetLowerBound(1) ?? 0;
            var rows = new List<IReadOnlyList<CellValue>>(selectionInfo.RowCount);
            for (int r = 0; r < selectionInfo.RowCount; r++)
            {
                token.ThrowIfCancellationRequested();
                var row = new List<CellValue>(selectionInfo.ColumnCount);
                for (int c = 0; c < selectionInfo.ColumnCount; c++)
                {
                    token.ThrowIfCancellationRequested();
                    row.Add(ParseCellValue(values == null ? null : values[r + rowBound, c + columnBound]));
                }
                rows.Add(row.AsReadOnly());
            }
            token.ThrowIfCancellationRequested();
            return CreateSnapshot(invocationId, source, ContextStatus.Available,
                new TableData(rows.AsReadOnly(), "Microsoft Excel", selectionInfo.Address));
        }
        catch (OperationCanceledException)
        {
            return CreateSnapshot(invocationId, source, ContextStatus.Canceled, new NoContent());
        }
        catch (Exception)
        {
            return CreateSnapshot(invocationId, source, ContextStatus.Failed, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExcelError);
        }
    }

    private CellValue ParseCellValue(object? val)
    {
        if (val == null)
        {
            return new EmptyCell();
        }

        if (val is string strVal)
        {
            return new TextCell(strVal);
        }

        if (val is double doubleVal)
        {
            return new NumberCell(doubleVal);
        }

        if (val is bool boolVal)
        {
            return new BooleanCell(boolVal);
        }

        if (val is int intVal)
        {
            // COM error type, e.g. -2146826281 for #DIV/0!
            // Excel error codes come back as Int32 usually starting with -2146826
            if (intVal < 0)
            {
                return new ErrorCell(intVal.ToString());
            }

            return new NumberCell(intVal);
        }

        return new TextCell(val.ToString() ?? "");
    }

    private ContextSnapshot CreateSnapshot(Guid invocationId, SourceWindowIdentity source, ContextStatus status, ContextContent content, string? userMessage = null)
    {
        return new ContextSnapshot(
            InvocationId: invocationId,
            Source: source,
            Status: status,
            Content: content,
            CapturedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            ProviderId: Id,
            UserMessage: userMessage,
            ProvidedCapabilities: new HashSet<string> { "table.export.v1" }); // basic capability if needed by actions
    }
}
