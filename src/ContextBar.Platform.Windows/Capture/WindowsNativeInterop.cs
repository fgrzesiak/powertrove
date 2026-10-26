using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ContextBar.Platform.Windows.Capture;

internal sealed class WindowsNativeInterop : INativeWindowInterop
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(nint hWnd);

    nint INativeWindowInterop.GetForegroundWindow() => GetForegroundWindow();

    int INativeWindowInterop.GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId) => GetWindowThreadProcessId(hWnd, out lpdwProcessId);

    int INativeWindowInterop.GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount) => GetWindowText(hWnd, lpString, nMaxCount);

    int INativeWindowInterop.GetWindowTextLength(nint hWnd) => GetWindowTextLength(hWnd);

    public string? GetProcessNameById(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return null; // Ignore errors like access denied when getting process name
        }
    }

    public int GetCurrentProcessId()
    {
        using var process = Process.GetCurrentProcess();
        return process.Id;
    }
}
