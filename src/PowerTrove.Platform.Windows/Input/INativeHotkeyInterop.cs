using System;

namespace PowerTrove.Platform.Windows.Input;

public interface INativeHotkeyInterop
{
    IntPtr CreateMessageWindow(Action<int, IntPtr> processMessage);
    bool DestroyMessageWindow();
    bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    bool UnregisterHotKey(IntPtr hWnd, int id);
    int GetLastError();
}
