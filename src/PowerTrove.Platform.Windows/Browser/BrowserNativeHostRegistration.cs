using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using PowerTrove.Core.Activation;

namespace PowerTrove.Platform.Windows.Browser;

/// <summary>
/// Registers an explicitly approved Chromium extension for the current Windows user.
/// No administrator rights, PowerShell, wildcard origin, or extension ID entry is required.
/// </summary>
public static class BrowserNativeHostRegistration
{
    private const string HostName = "com.powertrove.browser";
    private const string SettingsKey = @"Software\PowerTrove\BrowserHost";
    private const string ChromeKey = @"Software\Google\Chrome\NativeMessagingHosts\" + HostName;
    private const string EdgeKey = @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName;
    private static readonly UTF8Encoding Utf8 = new(false);

    private static string ManifestFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PowerTrove", "BrowserHost", HostName + ".json");

    /// <summary>The native host next to PowerTrove.exe; it shares the app's runtime like the pack worker.</summary>
    public static string HostExecutable(string applicationDirectory) =>
        Path.GetFullPath(Path.Combine(applicationDirectory, "PowerTrove.BrowserHost.exe"));

    public static bool IsConnected()
    {
        using var configuration = Registry.CurrentUser.OpenSubKey(SettingsKey);
        var origins = configuration?.GetValue("AllowedOrigins") as string;
        return File.Exists(ManifestFile) && !string.IsNullOrWhiteSpace(origins) &&
            (Owns(ChromeKey) || Owns(EdgeKey));
    }

    public static void Connect(BrowserPairingMessage pairing, string applicationDirectory)
    {
        // Packaged MSIX apps have virtualized registry writes and cannot safely
        // promise Chromium will discover this HKCU native messaging host.
        if (PowerTrove.Platform.Windows.Deployment.WindowsAppDistribution.Current.IsPackaged)
            throw new NotSupportedException("Browser pairing requires an unpackaged installer.");

        if (!ActivationProtocol.IsBrowserPairing(pairing))
            throw new ArgumentException("Invalid extension identity.", nameof(pairing));
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        string root = Path.GetFullPath(applicationDirectory);
        string host = HostExecutable(root);
        string desktop = Path.Combine(root, "PowerTrove.exe");
        if (!File.Exists(host) || !File.Exists(desktop))
            throw new FileNotFoundException("The installed PowerTrove browser host was not found.");
        string manifest = ManifestFile;
        string targetKey = pairing.Browser == "edge" ? EdgeKey : ChromeKey;

        using (var existingKey = Registry.CurrentUser.OpenSubKey(targetKey))
        {
            string? previousPath = existingKey?.GetValue("") as string;
            if (previousPath is not null && !Path.GetFullPath(previousPath).Equals(manifest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Another application owns this browser host registration.");
        }
        if (File.Exists(manifest) && (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing a linked native-messaging manifest.");

        var origins = new HashSet<string>(StringComparer.Ordinal);
        using (var existing = Registry.CurrentUser.OpenSubKey(SettingsKey))
        {
            if (existing?.GetValue("AllowedOrigins") is string previous)
                foreach (var origin in previous.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!ValidOrigin(origin)) throw new InvalidDataException("Invalid existing browser origin.");
                    origins.Add(origin);
                }
        }
        origins.Add("chrome-extension://" + pairing.ExtensionId + "/");
        if (origins.Count > 8) throw new InvalidOperationException("Too many browser connections.");
        var ordered = origins.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        string directory = Path.GetDirectoryName(manifest)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = HostName,
                ["description"] = "PowerTrove browser integration",
                ["path"] = host,
                ["type"] = "stdio",
                ["allowed_origins"] = ordered
            }), Utf8);
            File.Move(temporary, manifest, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        using (var key = Registry.CurrentUser.CreateSubKey(targetKey, writable: true))
            (key ?? throw new InvalidOperationException("Native host registration denied."))
                .SetValue("", manifest, RegistryValueKind.String);
        using (var key = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true))
        {
            if (key is null) throw new InvalidOperationException("Browser configuration denied.");
            key.SetValue("DesktopExe", desktop, RegistryValueKind.String);
            key.SetValue("AllowedOrigins", string.Join(";", ordered), RegistryValueKind.String);
        }
    }

    public static bool DisconnectIfOwnedBy(string desktopExecutable)
    {
        using var configuration = Registry.CurrentUser.OpenSubKey(SettingsKey);
        string? registered = configuration?.GetValue("DesktopExe") as string;
        if (string.IsNullOrEmpty(registered) || !string.Equals(
                Path.GetFullPath(registered), Path.GetFullPath(desktopExecutable),
                StringComparison.OrdinalIgnoreCase)) return false;
        Disconnect();
        return true;
    }

    public static void Disconnect()
    {
        // Only remove registrations that point to the manifest owned by this feature.
        foreach (string path in new[] { ChromeKey, EdgeKey })
            if (Owns(path)) Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(SettingsKey, throwOnMissingSubKey: false);
        if (File.Exists(ManifestFile) && (File.GetAttributes(ManifestFile) & FileAttributes.ReparsePoint) == 0)
            File.Delete(ManifestFile);
    }

    private static bool Owns(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return string.Equals(key?.GetValue("") as string, ManifestFile, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ValidOrigin(string value) =>
        value is { Length: 52 } && value.StartsWith("chrome-extension://", StringComparison.Ordinal) &&
        value.EndsWith("/", StringComparison.Ordinal) &&
        value["chrome-extension://".Length..^1].Length == 32 &&
        value["chrome-extension://".Length..^1].All(c => c is >= 'a' and <= 'p');
}
