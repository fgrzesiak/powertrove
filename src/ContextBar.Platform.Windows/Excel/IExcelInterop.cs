using System;
using System.Threading;
using ContextBar.Core;

namespace ContextBar.Platform.Windows.Excel;

public interface IExcelInterop
{
    // Called only on the capture worker. No COM references may escape this call.
    ExcelCaptureResult Capture(SourceWindowIdentity source, CancellationToken cancellationToken);
}

public sealed record ExcelCaptureResult(ContextStatus Status, ExcelSelectionInfo? Selection = null, string? Message = null);

public sealed record ExcelSelectionInfo(
    bool IsRange,
    int AreaCount,
    int RowCount,
    int ColumnCount,
    object?[,]? Values,
    string? Address);
