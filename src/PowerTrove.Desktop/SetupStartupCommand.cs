using PowerTrove.Platform.Windows.Shell;

namespace PowerTrove.Desktop;

/// <summary>Installer/uninstaller choice for starting at sign-in, before host election or any WinUI startup.</summary>
internal static class SetupStartupCommand
{
    public const string Argument = "--setup-startup";

    public static int Run(string operation) => Run(operation, new StartupRegistration(), Environment.ProcessPath);

    internal static int Run(string operation, IStartupRegistration registration, string? executable)
    {
        if (operation is not ("enable" or "disable") || executable is null) return 2;
        var state = operation == "enable" ? registration.Enable(executable) : registration.Disable(executable);
        // Windows' Startup apps switch and another copy's registration are the user's choice, not a setup failure.
        return state is StartupState.Unavailable ? 1 : 0;
    }
}
