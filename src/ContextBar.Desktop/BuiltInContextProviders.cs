using System;
using System.Collections.Generic;
using ContextBar.Abstractions;
using ContextBar.Platform.Windows.Capture;
using ContextBar.Platform.Windows.Excel;

namespace ContextBar.Desktop;

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
