using System.Diagnostics;
using FileKit.Media;

namespace PowerTrove.Platform.Windows.Media;

public enum FFmpegInstallOutcome { Installed, WingetMissing, Failed, Canceled, NotFoundAfterInstall }

/// <summary>Finds FFmpeg and, only on an explicit user request, installs it per user through winget.</summary>
public interface IFFmpegSetup
{
    /// <summary>Path of a usable FFmpeg, or null. Probes files only and starts no process.</summary>
    string? FindInstalled();

    /// <summary>Whether winget is present, so the guided installation can be offered.</summary>
    bool IsWingetAvailable();

    /// <summary>Runs winget for <see cref="FFmpegLocator.WingetPackageId"/>; cancellation stops winget.</summary>
    Task<FFmpegInstallOutcome> InstallAsync(CancellationToken cancellationToken);
}

/// <summary>Process port used by behavioral tests; returns winget's exit code.</summary>
public interface IWingetRunner
{
    string? Locate();
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Installs FFmpeg as a per-user winget package. PowerTrove downloads nothing itself and bundles no FFmpeg
/// binaries; winget fetches the package from its community source after the user clicks Install.
/// </summary>
public sealed class FFmpegSetup(IWingetRunner? runner = null, Func<string?>? locate = null) : IFFmpegSetup
{
    private readonly IWingetRunner _runner = runner ?? new ProcessWingetRunner();
    private readonly Func<string?> _locate = locate ?? FFmpegLocator.Find;

    /// <summary>Fixed arguments: exact package, community source, per-user scope, no prompts.</summary>
    public static IReadOnlyList<string> InstallArguments { get; } =
    [
        "install", "--id", FFmpegLocator.WingetPackageId, "--exact", "--source", "winget", "--scope", "user",
        "--silent", "--disable-interactivity", "--accept-package-agreements", "--accept-source-agreements"
    ];

    public string? FindInstalled() => _locate();

    public bool IsWingetAvailable() => _runner.Locate() is not null;

    public async Task<FFmpegInstallOutcome> InstallAsync(CancellationToken cancellationToken)
    {
        if (FindInstalled() is not null) return FFmpegInstallOutcome.Installed;
        string? winget = _runner.Locate();
        if (winget is null) return FFmpegInstallOutcome.WingetMissing;
        int exitCode;
        try
        {
            exitCode = await _runner.RunAsync(winget, InstallArguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return FFmpegInstallOutcome.Canceled;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return FFmpegInstallOutcome.Failed;
        }
        // winget reports "already installed" and other non-fatal states with non-zero codes; the file decides.
        if (FindInstalled() is not null) return FFmpegInstallOutcome.Installed;
        return exitCode == 0 ? FFmpegInstallOutcome.NotFoundAfterInstall : FFmpegInstallOutcome.Failed;
    }
}

/// <summary>Starts winget hidden, discards its output and kills it with its children on cancellation.</summary>
public sealed class ProcessWingetRunner : IWingetRunner
{
    public string? Locate()
    {
        // winget is an app execution alias in the per-user WindowsApps folder, which is also on PATH.
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(local)) candidates.Add(Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe"));
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { candidates.Add(Path.Combine(entry.Trim('"'), "winget.exe")); }
            catch (ArgumentException) { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("winget could not be started.");
        // Output is drained so winget never blocks on a full pipe; it is not logged.
        var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(CancellationToken.None),
            process.StandardError.ReadToEndAsync(CancellationToken.None));
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await drain.ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            throw;
        }
        await drain.ConfigureAwait(false);
        return process.ExitCode;
    }
}
