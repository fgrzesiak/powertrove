using PowerTrove.Platform.Windows.Shell;

namespace PowerTrove.Platform.Windows.Tests;

public class StartupRegistrationTests
{
    private const string Executable = @"C:\Users\Fixture\AppData\Local\Programs\PowerTrove\PowerTrove.exe";

    [Fact]
    public void RegistrationIdentity_UsesTheOwnedPerUserRunValueAndBackgroundStart()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.RunPath);
        Assert.Equal("PowerTrove", StartupRegistration.ValueName);
        Assert.Equal("\"" + Executable + "\" --background", StartupRegistration.Command(Executable));
    }

    [Fact]
    public void ReadingIsSideEffectFree_AndEnableThenDisableRoundTrips()
    {
        var registry = new FakeRegistry();
        IStartupRegistration service = new StartupRegistration(registry);
        Assert.Equal(StartupState.Disabled, service.GetState(Executable));
        Assert.Equal(0, registry.Writes);

        Assert.Equal(StartupState.Enabled, service.Enable(Executable));
        Assert.Equal(StartupRegistration.Command(Executable), registry.Command);
        Assert.Equal(StartupState.Enabled, service.Enable(Executable));
        Assert.Equal(1, registry.Writes);

        Assert.Equal(StartupState.Disabled, service.Disable(Executable));
        Assert.Null(registry.Command);
        Assert.Equal(StartupState.Disabled, service.Disable(Executable));
        Assert.Equal(1, registry.Deletes);
    }

    [Fact]
    public void AnotherCopysRegistration_IsNeverReplacedOrRemoved()
    {
        var registry = new FakeRegistry { Command = StartupRegistration.Command(@"D:\Portable\PowerTrove\PowerTrove.exe") };
        var service = new StartupRegistration(registry);
        Assert.Equal(StartupState.Conflict, service.GetState(Executable));
        Assert.Equal(StartupState.Conflict, service.Enable(Executable));
        Assert.Equal(StartupState.Conflict, service.Disable(Executable));
        Assert.Equal(0, registry.Writes + registry.Deletes);
    }

    [Theory]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, StartupState.DisabledByWindows)]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, StartupState.Enabled)]
    public void WindowsStartupAppsSwitch_IsReportedAndNeverReset(byte[] approval, StartupState expected)
    {
        var registry = new FakeRegistry { Command = StartupRegistration.Command(Executable), Approval = approval };
        var service = new StartupRegistration(registry);
        Assert.Equal(expected, service.GetState(Executable));
        Assert.Equal(expected, service.Enable(Executable));
        Assert.Equal(0, registry.Writes);
        Assert.Same(approval, registry.Approval);
    }

    [Fact]
    public void DisablingWhileWindowsTurnedItOff_RemovesOnlyTheOwnedValue()
    {
        var registry = new FakeRegistry { Command = StartupRegistration.Command(Executable), Approval = [0x03] };
        Assert.Equal(StartupState.Disabled, new StartupRegistration(registry).Disable(Executable));
        Assert.Null(registry.Command);
        Assert.NotNull(registry.Approval);
    }

    [Theory]
    [InlineData(@"relative\PowerTrove.exe")]
    [InlineData(@"\\server\share\PowerTrove.exe")]
    [InlineData(@"C:\Fixture\PowerTrove.cmd")]
    [InlineData("C:\\Fixture\\Power\"Trove.exe")]
    public void UnsafeExecutablePaths_AreRefusedWithoutWriting(string executable)
    {
        var registry = new FakeRegistry();
        Assert.Equal(StartupState.Unavailable, new StartupRegistration(registry).Enable(executable));
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public void RegistryFailures_AreUnavailableInsteadOfEnabled()
    {
        var registry = new FakeRegistry { FailWrites = true };
        var service = new StartupRegistration(registry);
        Assert.Equal(StartupState.Unavailable, service.Enable(Executable));
        registry.FailWrites = false;
        registry.NonStringValue = true;
        Assert.Equal(StartupState.Unavailable, service.GetState(Executable));
        Assert.Equal(StartupState.Unavailable, service.Disable(Executable));
    }

    private sealed class FakeRegistry : IStartupRegistry
    {
        public string? Command;
        public byte[]? Approval;
        public int Writes, Deletes;
        public bool FailWrites, NonStringValue;

        public string? ReadCommand() => NonStringValue ? throw new InvalidDataException() : Command;
        public byte[]? ReadApproval() => Approval;

        public void WriteCommand(string command)
        {
            if (FailWrites) throw new UnauthorizedAccessException();
            Writes++;
            Command = command;
        }

        public void DeleteCommand()
        {
            Deletes++;
            Command = null;
        }
    }
}
