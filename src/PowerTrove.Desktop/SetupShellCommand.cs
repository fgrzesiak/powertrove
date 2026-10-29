using System.Globalization;
using PowerTrove.Platform.Windows.Shell;

namespace PowerTrove.Desktop;

/// <summary>Explicit installer/uninstaller integration, before host election or any WinUI startup.</summary>
internal static class SetupShellCommand
{
    public const string Argument = "--setup-explorer-menu";

    public static int Run(string operation, string language) =>
        Run(operation, language, new ExplorerMenuRegistration(), Environment.ProcessPath);

    internal static int Run(string operation, string language, IExplorerMenuRegistration registration, string? executable)
    {
        if (operation is not ("install" or "remove") || language is not ("en" or "de") || executable is null) return 2;
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            var state = operation == "install" ? registration.Register(executable, UiText.ExplorerMenuLabel) : registration.Remove(executable);
            return state == (operation == "install" ? ExplorerMenuState.Registered : ExplorerMenuState.NotRegistered) ? 0 : 1;
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }
}
