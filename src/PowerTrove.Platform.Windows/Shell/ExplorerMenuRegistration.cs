using PowerTrove.Core.Activation;

namespace PowerTrove.Platform.Windows.Shell;

public enum ExplorerMenuState { NotRegistered, Registered, Incomplete, Conflict, Unavailable }

public interface IExplorerMenuRegistration
{
    ExplorerMenuState GetState(string executablePath);
    ExplorerMenuState Register(string executablePath, string menuTitle);
    ExplorerMenuState Remove(string executablePath);
}

public sealed record ExplorerVerbKey(IReadOnlyDictionary<string, string?> Values, IReadOnlyList<string> SubKeys);

/// <summary>Registry port used by behavioral tests; paths supplied by the service are fixed owned paths only.</summary>
public interface IExplorerVerbRegistry
{
    ExplorerVerbKey? Read(string path);
    void WriteString(string path, string name, string value);
    void DeleteTree(string path);
    void NotifyShell();
}

/// <summary>Explicit per-user classic registration. Reading or constructing this service performs no mutation.</summary>
public sealed class ExplorerMenuRegistration(IExplorerVerbRegistry? registry = null) : IExplorerMenuRegistration
{
    public const string VerbPath = @"Software\Classes\*\shell\PowerTrove.Open";
    public const string CommandPath = VerbPath + @"\command";
    public const string OwnerValue = "PowerTroveOwner";
    public const string ExecutableValue = "PowerTroveExecutable";
    public const string OwnerMarker = "PowerTrove.ExplorerVerb.v1";
    private static readonly HashSet<string> RootValues = new(StringComparer.OrdinalIgnoreCase)
        { OwnerValue, ExecutableValue, "MUIVerb", "Icon", "MultiSelectModel" };
    private readonly IExplorerVerbRegistry _registry = registry ?? new CurrentUserExplorerVerbRegistry();

    public ExplorerMenuState GetState(string executablePath) => Guard(() => Inspect(executablePath));

    public ExplorerMenuState Register(string executablePath, string menuTitle) => Guard(() =>
    {
        if (!ValidExecutable(executablePath) || string.IsNullOrWhiteSpace(menuTitle) || menuTitle.Length > 128
            || menuTitle.Any(char.IsControl)) return ExplorerMenuState.Unavailable;
        var state = Inspect(executablePath);
        if (state is ExplorerMenuState.Conflict or ExplorerMenuState.Unavailable) return state;
        // Write the owner marker first, so failures after this point leave an explicitly repairable owned entry.
        _registry.WriteString(VerbPath, OwnerValue, OwnerMarker);
        _registry.WriteString(VerbPath, ExecutableValue, executablePath);
        _registry.WriteString(VerbPath, "MUIVerb", menuTitle);
        _registry.WriteString(VerbPath, "Icon", Quote(executablePath) + ",0");
        _registry.WriteString(VerbPath, "MultiSelectModel", "Single");
        _registry.WriteString(CommandPath, "", Command(executablePath));
        if (Inspect(executablePath) != ExplorerMenuState.Registered) return ExplorerMenuState.Unavailable;
        _registry.NotifyShell();
        return ExplorerMenuState.Registered;
    });

    public ExplorerMenuState Remove(string executablePath) => Guard(() =>
    {
        var state = Inspect(executablePath);
        if (state is ExplorerMenuState.Conflict or ExplorerMenuState.Unavailable or ExplorerMenuState.NotRegistered) return state;
        _registry.DeleteTree(VerbPath);
        if (_registry.Read(VerbPath) is not null) return ExplorerMenuState.Unavailable;
        _registry.NotifyShell();
        return ExplorerMenuState.NotRegistered;
    });

    private ExplorerMenuState Inspect(string executablePath)
    {
        if (!ValidExecutable(executablePath)) return ExplorerMenuState.Unavailable;
        var root = _registry.Read(VerbPath);
        if (root is null) return ExplorerMenuState.NotRegistered;
        if (Value(root, OwnerValue) != OwnerMarker || root.Values.Keys.Any(name => !RootValues.Contains(name))
            || root.Values.Values.Any(value => value is null)
            || root.SubKeys.Any(name => !name.Equals("command", StringComparison.OrdinalIgnoreCase))) return ExplorerMenuState.Conflict;
        string? registeredExecutable = Value(root, ExecutableValue);
        if (registeredExecutable is not null && !registeredExecutable.Equals(executablePath, StringComparison.OrdinalIgnoreCase))
            return ExplorerMenuState.Conflict;
        var command = _registry.Read(CommandPath);
        if (command is not null && (command.SubKeys.Count != 0 || command.Values.Keys.Any(name => name.Length != 0)
            || command.Values.Values.Any(value => value is null))) return ExplorerMenuState.Conflict;
        // Missing owned values can result from an interrupted write. Unexpected existing commands/options are conflicts.
        if ((Value(root, "MultiSelectModel") is { } model && model != "Single")
            || (Value(root, "Icon") is { } icon && icon != Quote(executablePath) + ",0")
            || (command is not null && Value(command, "") is { } text && text != Command(executablePath)))
            return ExplorerMenuState.Conflict;
        return registeredExecutable is not null && !string.IsNullOrWhiteSpace(Value(root, "MUIVerb"))
            && Value(root, "MultiSelectModel") == "Single" && Value(root, "Icon") == Quote(executablePath) + ",0"
            && command is not null && Value(command, "") == Command(executablePath)
                ? ExplorerMenuState.Registered : ExplorerMenuState.Incomplete;
    }

    private static string? Value(ExplorerVerbKey key, string name) => key.Values.TryGetValue(name, out var value) ? value : null;
    private static string Quote(string path) => "\"" + path + "\"";
    private static string Command(string path) => Quote(path) + " --open-selection \"%1\"";
    private static bool ValidExecutable(string? path) => ActivationProtocol.IsLocalPath(path) && path!.Length <= 512
        && !path.Contains('%') && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    private static ExplorerMenuState Guard(Func<ExplorerMenuState> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or NotSupportedException) { return ExplorerMenuState.Unavailable; }
    }
}
