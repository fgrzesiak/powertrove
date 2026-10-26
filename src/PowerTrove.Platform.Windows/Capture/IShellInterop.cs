using System.Collections.Generic;
using PowerTrove.Core;

namespace PowerTrove.Platform.Windows.Capture;

public interface IShellInterop
{
    bool TryGetSelectedFiles(nint hwnd, out IReadOnlyList<SelectedFile> files, out string? error);
}
