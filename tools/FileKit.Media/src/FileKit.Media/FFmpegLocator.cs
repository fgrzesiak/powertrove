namespace FileKit.Media;

/// <summary>
/// Finds a local FFmpeg executable by probing paths only; it never starts a process.
/// The media pack bundles this tool and the host references it, so both agree on where a guided installation put FFmpeg.
/// </summary>
public static class FFmpegLocator
{
    /// <summary>winget package installed by the guided setup (a portable FFmpeg build).</summary>
    public const string WingetPackageId = "Gyan.FFmpeg";

    /// <summary>Probes the app folder, PATH and winget's per-user locations of the current process.</summary>
    public static string? Find() => Find(
        AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        File.Exists,
        SafeDirectories);

    /// <summary>
    /// Probes in order: the app folder, every PATH entry, winget's Links folder and winget's package folder.
    /// The winget locations matter because a running process keeps the PATH it started with, so a fresh
    /// installation is not visible through PATH until the next sign-in.
    /// </summary>
    public static string? Find(string? baseDirectory, string? pathVariable, string? localAppData,
        Func<string, bool> fileExists, Func<string, string, IEnumerable<string>> directories)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directories);
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (string candidate in Candidates(exe, baseDirectory, pathVariable, localAppData, directories))
        {
            try
            {
                if (fileExists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // An unreadable or malformed location is simply not a match.
            }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string exe, string? baseDirectory, string? pathVariable, string? localAppData,
        Func<string, string, IEnumerable<string>> directories)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory)) yield return Path.Combine(baseDirectory, exe);
        foreach (string entry in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? candidate = Combine(entry.Trim('"'), exe);
            if (candidate is not null) yield return candidate;
        }
        if (string.IsNullOrWhiteSpace(localAppData)) yield break;
        string winget = Path.Combine(localAppData, "Microsoft", "WinGet");
        yield return Path.Combine(winget, "Links", exe);
        // Without symbolic links winget keeps portable packages here: Packages\Gyan.FFmpeg_<source>\<build>\bin.
        foreach (string package in Ordered(directories(Path.Combine(winget, "Packages"), WingetPackageId + "_*")))
            foreach (string build in Ordered(directories(package, "*")))
                yield return Path.Combine(build, "bin", exe);
    }

    private static string? Combine(string directory, string exe)
    {
        try { return directory.Length == 0 ? null : Path.Combine(directory, exe); }
        catch (ArgumentException) { return null; }
    }

    // Newest build folder first; names carry the FFmpeg version.
    private static IEnumerable<string> Ordered(IEnumerable<string> paths) =>
        paths.OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    private static IEnumerable<string> SafeDirectories(string path, string pattern)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path, pattern).ToArray() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
