namespace PowerTrove.Platform.Windows.Shell;

public enum StartupState { Disabled, Enabled, DisabledByWindows, Conflict, Unavailable }

/// <summary>Start at sign-in through one owned per-user Run value.</summary>
public interface IStartupRegistration
{
    StartupState GetState(string executablePath);
    StartupState Enable(string executablePath);
    StartupState Disable(string executablePath);
}

/// <summary>Registry port used by behavioral tests; only the fixed PowerTrove value is read or written.</summary>
public interface IStartupRegistry
{
    /// <summary>The Run value, or null when absent. Throws <see cref="InvalidDataException"/> for a non-string value.</summary>
    string? ReadCommand();
    /// <summary>Windows' own Startup apps bookkeeping for the value, or null when Windows recorded nothing.</summary>
    byte[]? ReadApproval();
    void WriteCommand(string command);
    void DeleteCommand();
}

/// <summary>
/// Reading or constructing this service performs no mutation. A registration that points to another PowerTrove copy is
/// never replaced or removed, and Windows' Startup apps switch is respected instead of being reset.
/// </summary>
public sealed class StartupRegistration(IStartupRegistry? registry = null) : IStartupRegistration
{
    public const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovalPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "PowerTrove";
    /// <summary>Starts the host with the tray icon and shortcut only; no palette and no selection capture.</summary>
    public const string BackgroundArgument = "--background";
    private readonly IStartupRegistry _registry = registry ?? new CurrentUserStartupRegistry();

    public static string Command(string executablePath) => "\"" + executablePath + "\" " + BackgroundArgument;

    public StartupState GetState(string executablePath) => Guard(() => Inspect(executablePath));

    public StartupState Enable(string executablePath) => Guard(() =>
    {
        var state = Inspect(executablePath);
        if (state is not StartupState.Disabled) return state;
        _registry.WriteCommand(Command(executablePath));
        return Inspect(executablePath) == StartupState.Enabled ? StartupState.Enabled : StartupState.Unavailable;
    });

    public StartupState Disable(string executablePath) => Guard(() =>
    {
        var state = Inspect(executablePath);
        if (state is StartupState.Conflict or StartupState.Unavailable or StartupState.Disabled) return state;
        _registry.DeleteCommand();
        return _registry.ReadCommand() is null ? StartupState.Disabled : StartupState.Unavailable;
    });

    private StartupState Inspect(string executablePath)
    {
        if (!ValidExecutable(executablePath)) return StartupState.Unavailable;
        string? command = _registry.ReadCommand();
        if (command is null) return StartupState.Disabled;
        if (!command.Equals(Command(executablePath), StringComparison.OrdinalIgnoreCase)) return StartupState.Conflict;
        // Windows records the Startup apps switch per value; an odd first byte means the user turned it off there.
        return _registry.ReadApproval() is { Length: > 0 } approval && (approval[0] & 1) == 1
            ? StartupState.DisabledByWindows : StartupState.Enabled;
    }

    private static bool ValidExecutable(string executablePath) =>
        Path.IsPathFullyQualified(executablePath) && !executablePath.StartsWith(@"\\", StringComparison.Ordinal)
        && !executablePath.Contains('"') && executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && executablePath.Length <= 1024;

    private static StartupState Guard(Func<StartupState> operation)
    {
        try { return operation(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or InvalidDataException) { return StartupState.Unavailable; }
    }
}
