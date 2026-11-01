using PowerTrove.Core.Input;
using Windows.System;

namespace PowerTrove.Desktop;

/// <summary>Shortcut labels such as "Ctrl + Alt + Space", as shown in Settings and notices.</summary>
public static class ShortcutText
{
    public static string Format(HotkeyConfiguration config)
    {
        string text = "";
        if (config.Modifiers.HasFlag(KeyModifiers.Control)) text += "Ctrl + ";
        if (config.Modifiers.HasFlag(KeyModifiers.Windows)) text += "Win + ";
        if (config.Modifiers.HasFlag(KeyModifiers.Alt)) text += "Alt + ";
        if (config.Modifiers.HasFlag(KeyModifiers.Shift)) text += "Shift + ";
        return text + ((VirtualKey)config.Key).ToString();
    }
}
