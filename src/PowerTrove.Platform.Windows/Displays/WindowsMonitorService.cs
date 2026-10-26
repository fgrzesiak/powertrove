using System;

namespace PowerTrove.Platform.Windows.Displays;

public sealed class WindowsMonitorService : IWindowMonitorService
{
    private readonly INativeMonitorInterop _interop;

    public WindowsMonitorService() : this(new WindowsNativeMonitorInterop())
    {
    }

    public WindowsMonitorService(INativeMonitorInterop interop)
    {
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
    }

    public MonitorWorkArea GetMonitorWorkAreaForWindow(nint hwnd)
    {
        nint hMonitor = _interop.MonitorFromWindow(hwnd, MonitorOptions.MONITOR_DEFAULTTONEAREST);

        if (hMonitor == IntPtr.Zero || !_interop.GetMonitorInfo(hMonitor, out var monitorInfo))
        {
            return GetPrimaryMonitorWorkArea();
        }

        _interop.GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY);

        return new MonitorWorkArea(
            monitorInfo.rcWork.left,
            monitorInfo.rcWork.top,
            monitorInfo.rcWork.right - monitorInfo.rcWork.left,
            monitorInfo.rcWork.bottom - monitorInfo.rcWork.top,
            (int)dpiX,
            (int)dpiY);
    }

    public MonitorWorkArea GetPrimaryMonitorWorkArea()
    {
        nint hMonitor = _interop.MonitorFromWindow(IntPtr.Zero, MonitorOptions.MONITOR_DEFAULTTOPRIMARY);

        if (hMonitor == IntPtr.Zero || !_interop.GetMonitorInfo(hMonitor, out var monitorInfo))
        {
            // Absolute fallback
            return new MonitorWorkArea(0, 0, 1920, 1080, 96, 96);
        }

        _interop.GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY);

        return new MonitorWorkArea(
            monitorInfo.rcWork.left,
            monitorInfo.rcWork.top,
            monitorInfo.rcWork.right - monitorInfo.rcWork.left,
            monitorInfo.rcWork.bottom - monitorInfo.rcWork.top,
            (int)dpiX,
            (int)dpiY);
    }
}
