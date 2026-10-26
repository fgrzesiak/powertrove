using System;
using System.Runtime.InteropServices;

namespace ContextBar.Platform.Windows.Displays;

internal sealed class WindowsNativeMonitorInterop : INativeMonitorInterop
{
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, MonitorOptions dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    nint INativeMonitorInterop.MonitorFromWindow(nint hwnd, MonitorOptions dwFlags)
    {
        return MonitorFromWindow(hwnd, dwFlags);
    }

    bool INativeMonitorInterop.GetMonitorInfo(nint hMonitor, out MONITORINFO lpmi)
    {
        lpmi = new MONITORINFO();
        lpmi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        return GetMonitorInfo(hMonitor, ref lpmi);
    }

    void INativeMonitorInterop.GetDpiForMonitor(nint hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY)
    {
        dpiX = 96;
        dpiY = 96;
        try
        {
            // It could be that shcore is not available on very old Windows, but we only target modern Windows
            var result = GetDpiForMonitor(hmonitor, dpiType, out dpiX, out dpiY);
            if (result != 0)
            {
                dpiX = 96;
                dpiY = 96;
            }
        }
        catch (DllNotFoundException)
        {
            // fallback
        }
        catch (EntryPointNotFoundException)
        {
            // fallback
        }
    }
}
