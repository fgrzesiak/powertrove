using System;
using System.IO;

namespace PowerTrove.Application.Localization;

/// <summary>One-time, per-user educational hint. CreateNew makes concurrent surfaces race safely.</summary>
public static class BrowserSetupHintState
{
    private const string Marker = "browser-extension-setup-hint-shown";

    public static bool TryMarkShown(string userRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userRoot);
        Directory.CreateDirectory(userRoot);
        string path = Path.Combine(userRoot, Marker);
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }
}
