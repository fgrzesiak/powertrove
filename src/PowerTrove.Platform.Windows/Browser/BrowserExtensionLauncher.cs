using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace PowerTrove.Platform.Windows.Browser;

/// <summary>Opens an official extension URL in the requested installed browser, or the default browser.</summary>
public static class BrowserExtensionLauncher
{
    public static void Open(string browser, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("HTTPS URL required.", nameof(uri));
        string exe = browser switch
        {
            "chrome" => "chrome.exe",
            "edge" => "msedge.exe",
            _ => throw new ArgumentOutOfRangeException(nameof(browser))
        };
        var path = FindRegisteredAppPath(exe);
        if (path is not null)
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = false };
            start.ArgumentList.Add(uri.AbsoluteUri);
            Process.Start(start);
        }
        else
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private static string? FindRegisteredAppPath(string exe)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
            if (key?.GetValue(null) is string path && System.IO.File.Exists(path))
                return path;
        }
        return null;
    }
}
