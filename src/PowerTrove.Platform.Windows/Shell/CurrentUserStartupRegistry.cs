using Microsoft.Win32;

namespace PowerTrove.Platform.Windows.Shell;

internal sealed class CurrentUserStartupRegistry : IStartupRegistry
{
    public string? ReadCommand()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = hive.OpenSubKey(StartupRegistration.RunPath, writable: false);
        if (key?.GetValue(StartupRegistration.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)
            return null;
        return key.GetValueKind(StartupRegistration.ValueName) == RegistryValueKind.String && value is string command
            ? command : throw new InvalidDataException("The startup value is not a string.");
    }

    public byte[]? ReadApproval()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = hive.OpenSubKey(StartupRegistration.ApprovalPath, writable: false);
        return key?.GetValue(StartupRegistration.ValueName) as byte[];
    }

    public void WriteCommand(string command)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = hive.CreateSubKey(StartupRegistration.RunPath, writable: true)
            ?? throw new IOException("Startup registration is unavailable.");
        key.SetValue(StartupRegistration.ValueName, command, RegistryValueKind.String);
    }

    public void DeleteCommand()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = hive.OpenSubKey(StartupRegistration.RunPath, writable: true);
        key?.DeleteValue(StartupRegistration.ValueName, throwOnMissingValue: false);
    }
}
