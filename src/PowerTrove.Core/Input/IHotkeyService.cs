using System;

namespace PowerTrove.Core.Input;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public enum RegisterHotkeyResult
{
    Success,
    Conflict,
    Error,
    InitializationFailed
}

public sealed record HotkeyConfiguration(KeyModifiers Modifiers, uint Key);

public interface IHotkeyService : IDisposable
{
    RegisterHotkeyResult Register(HotkeyConfiguration configuration);
    /// <summary>Releases owned shortcuts. Failed releases retain state for retry.</summary>
    /// <exception cref="InvalidOperationException">A native shortcut could not be released.</exception>
    void Unregister();

    event EventHandler? HotkeyTriggered;
}
