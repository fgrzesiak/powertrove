using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ContextBar.Platform.Windows.Shell;

internal sealed class CurrentUserExplorerVerbRegistry : IExplorerVerbRegistry
{
    public ExplorerVerbKey? Read(string path)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = hive.OpenSubKey(path, writable: false);
        if (key is null) return null;
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in key.GetValueNames())
            values[name] = key.GetValueKind(name) == RegistryValueKind.String
                ? key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string : null;
        return new ExplorerVerbKey(values, key.GetSubKeyNames());
    }

    public void WriteString(string path, string name, string value)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = hive.CreateSubKey(path, writable: true)
            ?? throw new IOException("Explorer registration is unavailable.");
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteTree(string path)
    {
        // Defense in depth: never delete parent associations or a caller-supplied registry subtree.
        if (path != ExplorerMenuRegistration.VerbPath) throw new ArgumentException("Invalid registration key.");
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        hive.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }

    public void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
