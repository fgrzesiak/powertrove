using System.Diagnostics;
using PowerTrove.Core.Input;

namespace PowerTrove.Platform.Windows.Input;

/// <summary>Checks process names of the current machine; it reads no command lines, windows or files.</summary>
public sealed class WindowsRunningProcessProbe : IRunningProcessProbe
{
    public bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try { return processes.Length > 0; }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }
}
