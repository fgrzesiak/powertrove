using System.Globalization;
using PowerTrove.Desktop;
using PowerTrove.Platform.Windows.Shell;

namespace PowerTrove.Architecture.Tests;

public sealed class SetupShellCommandTests
{
    [Theory]
    [InlineData("en", "Open in PowerTrove")]
    [InlineData("de", "In PowerTrove öffnen")]
    public void InstallUsesTheSelectedSetupLanguageAndTheExistingRegistrationPort(string language, string expected)
    {
        var registration = new Registration { Result = ExplorerMenuState.Registered };
        var culture = CultureInfo.CurrentUICulture;
        Assert.Equal(0, SetupShellCommand.Run("install", language, registration, @"C:\Fixture\PowerTrove.exe"));
        Assert.Equal(expected, registration.Title);
        Assert.Equal(1, registration.Installs);
        Assert.Equal(culture, CultureInfo.CurrentUICulture);
    }

    [Theory]
    [InlineData(ExplorerMenuState.Conflict)]
    [InlineData(ExplorerMenuState.Unavailable)]
    public void RegistrationFailureIsReportedToSetup(ExplorerMenuState state) =>
        Assert.Equal(1, SetupShellCommand.Run("install", "en", new Registration { Result = state }, @"C:\Fixture\PowerTrove.exe"));

    [Fact]
    public void UninstallUsesTheSameOwnershipCheckedRemoval()
    {
        var registration = new Registration { Result = ExplorerMenuState.NotRegistered };
        Assert.Equal(0, SetupShellCommand.Run("remove", "de", registration, @"C:\Fixture\PowerTrove.exe"));
        Assert.Equal(1, registration.Removals);
        Assert.Equal(0, registration.Installs);
    }

    [Theory]
    [InlineData("unknown", "en")]
    [InlineData("install", "unknown")]
    public void InvalidArgumentsNeverTouchRegistration(string operation, string language)
    {
        var registration = new Registration();
        Assert.Equal(2, SetupShellCommand.Run(operation, language, registration, @"C:\Fixture\PowerTrove.exe"));
        Assert.Equal(0, registration.Installs + registration.Removals);
    }

    private sealed class Registration : IExplorerMenuRegistration
    {
        public ExplorerMenuState Result;
        public string? Title;
        public int Installs, Removals;
        public ExplorerMenuState GetState(string path) => Result;
        public ExplorerMenuState Register(string path, string title) { Installs++; Title = title; return Result; }
        public ExplorerMenuState Remove(string path) { Removals++; return Result; }
    }
}
