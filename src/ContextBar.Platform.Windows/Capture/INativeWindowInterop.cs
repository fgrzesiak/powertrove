using System;
using System.Text;

namespace ContextBar.Platform.Windows.Capture;

public interface INativeWindowInterop
{
    nint GetForegroundWindow();
    int GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);
    int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);
    int GetWindowTextLength(nint hWnd);
    string? GetProcessNameById(int processId);
    int GetCurrentProcessId();
}
