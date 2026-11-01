using System;
using System.Collections.Generic;
using System.Linq;

namespace PowerTrove.Core.Input;

/// <summary>A launcher that claims Alt + Space by default. Its own settings may differ.</summary>
public sealed record KnownLauncher(string DisplayName, string ProcessName);

/// <summary>Reports whether a process with the given name runs in the current session.</summary>
public interface IRunningProcessProbe
{
    bool IsRunning(string processName);
}

public enum HotkeyIssue
{
    None,
    /// <summary>Another application already registered the shortcut.</summary>
    Conflict,
    /// <summary>Windows refused the shortcut for another reason.</summary>
    RegistrationFailed,
    /// <summary>The shortcut works, but a running launcher uses Alt + Space by default.</summary>
    LauncherMayConflict
}

public sealed record HotkeyAdvice(HotkeyIssue Issue, string? LauncherName = null, HotkeyConfiguration? Suggestion = null)
{
    public static HotkeyAdvice None { get; } = new(HotkeyIssue.None);
}

/// <summary>
/// Explains why the global shortcut may not open PowerTrove and proposes a free alternative.
/// Launchers that use a keyboard hook instead of RegisterHotKey do not cause a registration
/// conflict, so a running launcher is reported even when registration succeeded.
/// </summary>
public static class HotkeyAdvisor
{
    public static HotkeyConfiguration Default { get; } = new(KeyModifiers.Alt, 0x20 /* VK_SPACE */);

    /// <summary>Alternatives in order of preference; each avoids shortcuts that common applications use.</summary>
    public static IReadOnlyList<HotkeyConfiguration> Alternatives { get; } =
    [
        new(KeyModifiers.Control | KeyModifiers.Alt, 0x20 /* Space */),
        new(KeyModifiers.Alt | KeyModifiers.Shift, 0x20 /* Space */),
        new(KeyModifiers.Control | KeyModifiers.Alt, 0x50 /* P */)
    ];

    public static IReadOnlyList<KnownLauncher> AltSpaceLaunchers { get; } =
    [
        new("PowerToys Run", "PowerToys.PowerLauncher"),
        new("Flow Launcher", "Flow.Launcher"),
        new("Wox", "Wox")
    ];

    public static HotkeyAdvice Evaluate(
        HotkeyConfiguration current,
        RegisterHotkeyResult result,
        IRunningProcessProbe processes,
        Func<HotkeyConfiguration, bool> isAvailable,
        bool launcherHintDismissed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(isAvailable);

        switch (result)
        {
            case RegisterHotkeyResult.Conflict:
                return new HotkeyAdvice(HotkeyIssue.Conflict, Suggestion: FirstAvailable(current, isAvailable));
            case RegisterHotkeyResult.Error:
            case RegisterHotkeyResult.InitializationFailed:
                return new HotkeyAdvice(HotkeyIssue.RegistrationFailed);
        }

        if (launcherHintDismissed || current != Default) return HotkeyAdvice.None;
        var launcher = AltSpaceLaunchers.FirstOrDefault(l => IsRunning(processes, l.ProcessName));
        return launcher is null
            ? HotkeyAdvice.None
            : new HotkeyAdvice(HotkeyIssue.LauncherMayConflict, launcher.DisplayName, FirstAvailable(current, isAvailable));
    }

    public static HotkeyConfiguration? FirstAvailable(HotkeyConfiguration current, Func<HotkeyConfiguration, bool> isAvailable) =>
        Alternatives.FirstOrDefault(candidate => candidate != current && isAvailable(candidate));

    // A failed probe must not hide a working shortcut behind an error.
    private static bool IsRunning(IRunningProcessProbe processes, string name)
    {
        try { return processes.IsRunning(name); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return false; }
    }
}
