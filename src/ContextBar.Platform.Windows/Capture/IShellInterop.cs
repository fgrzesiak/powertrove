using System.Collections.Generic;
using ContextBar.Core;

namespace ContextBar.Platform.Windows.Capture;

public interface IShellInterop
{
    bool TryGetSelectedFiles(nint hwnd, out IReadOnlyList<SelectedFile> files, out string? error);
}
