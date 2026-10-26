using System;

namespace ContextBar.Platform.Windows.Displays;

public interface IWindowMonitorService
{
    MonitorWorkArea GetMonitorWorkAreaForWindow(nint hwnd);
    MonitorWorkArea GetPrimaryMonitorWorkArea();
}
