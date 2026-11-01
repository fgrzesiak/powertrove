using PowerTrove.Desktop;
using PowerTrove.Platform.Windows.Shell;

namespace PowerTrove.Architecture.Tests;

public sealed class SetupStartupCommandTests
{
    private const string Executable = @"C:\Fixture\PowerTrove.exe";

    [Theory]
    [InlineData("enable", StartupState.Enabled, 0)]
    [InlineData("disable", StartupState.Disabled, 0)]
    [InlineData("enable", StartupState.Unavailable, 1)]
    // The user's Windows Startup apps switch and another copy's entry are kept; Setup does not report a failure.
    [InlineData("enable", StartupState.DisabledByWindows, 0)]
    [InlineData("disable", StartupState.Conflict, 0)]
    public void SetupUsesTheSameRegistrationAsSettings(string operation, StartupState result, int exitCode)
    {
        var registration = new Registration { Result = result };
        Assert.Equal(exitCode, SetupStartupCommand.Run(operation, registration, Executable));
        Assert.Equal(operation == "enable" ? (1, 0) : (0, 1), (registration.Enables, registration.Disables));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("")]
    public void InvalidArgumentsNeverTouchRegistration(string operation)
    {
        var registration = new Registration();
        Assert.Equal(2, SetupStartupCommand.Run(operation, registration, Executable));
        Assert.Equal(2, SetupStartupCommand.Run("enable", registration, null));
        Assert.Equal(0, registration.Enables + registration.Disables);
    }

    [Fact]
    public void Installer_PreselectsStartupOnlyForNewInstallations_AndRemovesItOnUninstall()
    {
        string script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "packaging", "windows", "PowerTrove.iss"));
        string task = script.Split('\n').Single(line => line.StartsWith("Name: \"startup\";", StringComparison.Ordinal));
        Assert.DoesNotContain("unchecked", task, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("{cm:Installer_Startup}", task, StringComparison.Ordinal);
        // Updates mirror the current registration, so turning it off in Settings survives a reinstall.
        Assert.Contains("RegValueExists(HKCU, RunKey, 'PowerTrove')", script, StringComparison.Ordinal);
        Assert.Contains("WizardSelectTasks('!startup')", script, StringComparison.Ordinal);
        Assert.Contains("'--setup-startup enable'", script, StringComparison.Ordinal);
        int uninstall = script.IndexOf("procedure CurUninstallStepChanged", StringComparison.Ordinal);
        Assert.True(uninstall >= 0);
        Assert.Contains("'--setup-startup disable'", script[uninstall..], StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PowerTrove.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class Registration : IStartupRegistration
    {
        public StartupState Result;
        public int Enables, Disables;
        public StartupState GetState(string path) => Result;
        public StartupState Enable(string path) { Enables++; return Result; }
        public StartupState Disable(string path) { Disables++; return Result; }
    }
}
