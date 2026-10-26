using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using PowerTrove.Core;

namespace PowerTrove.Platform.Windows.Excel;

public sealed class ExcelInterop : IExcelInterop
{
    public const int MaximumCells = 1_000_000;
    private delegate bool EnumWindowCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(nint hwnd, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object window);

    private static bool MatchesSource(SourceWindowIdentity source) =>
        source.WindowHandle != 0 && source.ProcessId > 0 &&
        GetWindowThreadProcessId(source.WindowHandle, out uint pid) != 0 && pid == (uint)source.ProcessId;

    private static bool IsRange(object selection)
    {
        nint unknown = Marshal.GetIUnknownForObject(selection);
        nint range = 0;
        try
        {
            var iid = new Guid("00020846-0000-0000-C000-000000000046");
            int result = Marshal.QueryInterface(unknown, ref iid, out range);
            if (result == unchecked((int)0x80004002)) return false;
            Marshal.ThrowExceptionForHR(result);
            return true;
        }
        finally
        {
            if (range != 0) Marshal.Release(range);
            Marshal.Release(unknown);
        }
    }

    public ExcelCaptureResult Capture(SourceWindowIdentity source, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return new(ContextStatus.Unsupported, Message: PowerTrove.Platform.Windows.Resources.Strings.ExcelNotAvailable);
        // RCWs never leave this apartment; release each acquisition in reverse order.
        var owned = new List<object>();
        object? Own(object? value)
        {
            if (value != null && Marshal.IsComObject(value)) owned.Add(value);
            return value;
        }
        ExcelCaptureResult Changed() => new(ContextStatus.Unsupported, Message: PowerTrove.Platform.Windows.Resources.Strings.ExcelWindowChanged);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesSource(source)) return Changed();
            var children = new List<nint>();
            EnumChildWindows(source.WindowHandle, (hwnd, _) =>
            {
                var name = new StringBuilder(64);
                GetClassName(hwnd, name, name.Capacity);
                if (name.ToString() == "EXCEL7") children.Add(hwnd);
                return true;
            }, 0);
            dynamic? window = null;
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GetWindowThreadProcessId(child, out uint pid);
                if (pid != (uint)source.ProcessId) continue;
                var iid = new Guid("00020400-0000-0000-C000-000000000046");
                int hr = AccessibleObjectFromWindow(child, 0xFFFFFFF0, ref iid, out object nativeWindow);
                Own(nativeWindow);
                Marshal.ThrowExceptionForHR(hr);
                if (nativeWindow == null) continue;
                dynamic candidate = nativeWindow;
                if ((nint)(int)candidate.Hwnd == source.WindowHandle)
                {
                    window = candidate;
                    break;
                }
            }
            if (window == null)
                return new(ContextStatus.Unsupported, Message: PowerTrove.Platform.Windows.Resources.Strings.ExcelWindowUnavailable);
            dynamic app = Own((object)window.Application)!;
            bool StillMatches()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MatchesSource(source) || (nint)(int)window.Hwnd != source.WindowHandle) return false;
                dynamic? active = Own((object?)app.ActiveWindow);
                return active != null && (nint)(int)active.Hwnd == source.WindowHandle;
            }
            if (!StillMatches()) return Changed();
            dynamic? selection = Own((object?)app.Selection);
            if (selection == null) return StillMatches() ? new(ContextStatus.Empty) : Changed();
            if (!IsRange((object)selection))
                return new(ContextStatus.Unsupported, Message: PowerTrove.Platform.Windows.Resources.Strings.ExcelItemNotRange);
            dynamic areas = Own((object)selection.Areas)!;
            int areaCount = areas.Count;
            if (areaCount != 1)
                return new(ContextStatus.Unsupported, Message: PowerTrove.Platform.Windows.Resources.Strings.ExcelDisjointSelection);
            dynamic rows = Own((object)selection.Rows)!;
            dynamic columns = Own((object)selection.Columns)!;
            int rowCount = rows.Count;
            int columnCount = columns.Count;
            // Bound input before Value2 marshaling or any cell-sized allocation.
            if (rowCount <= 0 || columnCount <= 0 || (long)rowCount * columnCount > MaximumCells)
                return new(ContextStatus.Unsupported, Message: PowerTrove.Platform.Windows.Resources.Strings.ExcelRangeTooLarge);
            cancellationToken.ThrowIfCancellationRequested();
            string address = selection.Address;
            object? value = Own((object?)selection.Value2);
            cancellationToken.ThrowIfCancellationRequested();
            object?[,]? values = null;
            if (value is object[,] array)
            {
                if (array.GetLength(0) != rowCount || array.GetLength(1) != columnCount)
                    return new(ContextStatus.Failed, Message: PowerTrove.Platform.Windows.Resources.Strings.ExcelInconsistentDimensions);
                values = array; // Keep SAFEARRAY bounds instead of making another full-size copy.
            }
            else if (value != null)
            {
                if (rowCount != 1 || columnCount != 1 || Marshal.IsComObject(value))
                    return new(ContextStatus.Failed, Message: PowerTrove.Platform.Windows.Resources.Strings.ExcelInvalidRangeValues);
                values = new object?[1, 1] { { value } };
            }
            if (!StillMatches()) return Changed();
            return new(ContextStatus.Available, new ExcelSelectionInfo(true, areaCount, rowCount, columnCount, values, address));
        }
        catch (OperationCanceledException) { return new(ContextStatus.Canceled); }
        catch (Exception exception)
        {
            bool denied = exception is UnauthorizedAccessException || exception.HResult == unchecked((int)0x80070005);
            return new(denied ? ContextStatus.Denied : ContextStatus.Failed,
                Message: denied ? PowerTrove.Platform.Windows.Resources.Strings.ExcelAccessDenied : PowerTrove.Platform.Windows.Resources.Strings.ExcelError);
        }
        finally
        {
            for (int i = owned.Count - 1; i >= 0; i--)
            {
                try { Marshal.ReleaseComObject(owned[i]); }
                catch (InvalidComObjectException) { }
                catch (COMException) { }
            }
        }
    }
}
