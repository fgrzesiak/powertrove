using System;
using System.Collections.Generic;
using PowerTrove.Abstractions;
using PowerTrove.Platform.Windows.Capture;
using PowerTrove.Platform.Windows.Excel;

namespace PowerTrove.Desktop;

/// <summary>Context providers used by desktop startup.</summary>
public static class BuiltInContextProviders
{
    public static IReadOnlyList<IContextProvider> Create()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<IContextProvider>();

        return new IContextProvider[]
        {
            new ExplorerContextProvider(new WindowsShellInterop()),
            new ExcelSelectionProvider()
        };
    }
}
