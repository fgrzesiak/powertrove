using ContextBar.Platform.Windows.Resources;

namespace ContextBar.Platform.Windows.Shell;

public enum ShellNavigationError { None, UnsupportedPlatform, InvalidSelection, PidlCreationFailed, OpenFailed }

/// <summary>Failure category only: never contains selected paths or raw native error text.</summary>
public readonly record struct ShellNavigationResult(ShellNavigationError Error)
{
    public bool IsSuccess => Error == ShellNavigationError.None;
    public string? Message => IsSuccess ? null : Strings.ShellRevealFailed;
    public static ShellNavigationResult FromHResult(int hresult) =>
        new(hresult >= 0 ? ShellNavigationError.None : ShellNavigationError.OpenFailed);
}

/// <summary>Shell PIDL ownership boundary; injectable for tests without touching Explorer.</summary>
public interface IShellSelectionInterop
{
    bool IsSupported { get; }
    nint CreatePidl(string path);
    int OpenFolderAndSelect(nint folder, IReadOnlyList<nint> items);
    void FreePidl(nint pointer);
}
