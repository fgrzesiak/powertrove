using System;

namespace PowerTrove.Platform.Windows.Displays;

public interface IWindowMonitorService
{
    MonitorWorkArea GetMonitorWorkAreaForWindow(nint hwnd);
    MonitorWorkArea GetPrimaryMonitorWorkArea();
}
