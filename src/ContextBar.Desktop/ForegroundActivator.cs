using System;
using System.Runtime.InteropServices;

namespace ContextBar.Desktop;

/// <summary>
/// Brings a ContextBar window to the foreground so it receives keyboard input right away.
/// WinUI's Activate() alone is often refused by the foreground lock, leaving the window visible
/// but the keyboard with the previous application. Called only in response to the user's shortcut
/// or tray click, which Windows allows.
/// </summary>
internal static class ForegroundActivator
{
    public static void BringToFront(Microsoft.UI.Xaml.Window window)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero || GetForegroundWindow() == hwnd)
            return;
        if (SetForegroundWindow(hwnd))
            return;

        // Fallback: share the input state with the current foreground thread for the switch.
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint ownThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != ownThread && AttachThreadInput(ownThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(ownThread, foregroundThread, false);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
