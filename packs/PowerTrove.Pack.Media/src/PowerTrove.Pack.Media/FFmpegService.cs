using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Pack.Media;

/// <summary>
/// Provides secure invocation of local FFmpeg processes.
/// Note on Redistribution: FFmpeg is licensed under the LGPL or GPL (depending on compilation flags).
/// If PowerTrove ever bundles or distributes FFmpeg binaries, it must comply with these licenses
/// (e.g., providing source code, dynamic linking for LGPL). By default, this service expects the user
/// to install FFmpeg independently and add it to the system PATH.
/// </summary>
public class FFmpegService : IFFmpegService
{
    private readonly string? _executablePath;
    private string? _version;
    private bool? _isAvailable;

    public FFmpegService(string? customPath = null)
    {
        _executablePath = customPath ?? FindFFmpeg();
    }

    public bool IsAvailable
    {
        get
        {
            if (_isAvailable.HasValue) return _isAvailable.Value;
            _isAvailable = !string.IsNullOrEmpty(_executablePath) && File.Exists(_executablePath);
            return _isAvailable.Value;
        }
    }

    public string? ExecutablePath => _executablePath;

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable) return null;
        if (_version != null) return _version;

        try
        {
            var result = await ExecuteAsync(new[] { "-version" }, cancellationToken).ConfigureAwait(false);
            if (result.Success && !string.IsNullOrEmpty(result.Output))
            {
                var lines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                {
                    var firstLine = lines[0];
                    var versionMarker = "ffmpeg version ";
                    var markerIndex = firstLine.IndexOf(versionMarker, StringComparison.OrdinalIgnoreCase);
                    if (markerIndex >= 0)
                    {
                        var versionStr = firstLine.Substring(markerIndex + versionMarker.Length);
                        var spaceIndex = versionStr.IndexOf(' ');
                        if (spaceIndex >= 0)
                        {
                            versionStr = versionStr.Substring(0, spaceIndex);
                        }
                        _version = versionStr;
                    }
                    else
                    {
                        _version = firstLine; // fallback
                    }
                }
            }
        }
        catch
        {
            // Ignore failure during version check
        }
        return _version;
    }

    private string? FindFFmpeg()
    {
        var exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        // 1. Check AppContext BaseDirectory
        var appDirExe = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(appDirExe)) return appDirExe;

        // 2. Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            var paths = pathEnv.Split(Path.PathSeparator);
            foreach (var path in paths)
            {
                try
                {
                    var fullPath = Path.Combine(path, exeName);
                    if (File.Exists(fullPath)) return fullPath;
                }
                catch (Exception)
                {
                    // Ignore invalid paths in PATH env var
                }
            }
        }

        return null;
    }

    public bool IsProbeAvailable => ExecutablePath is { } path && File.Exists(ProbePath(path));
    private static string ProbePath(string executable) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
    public Task<FFmpegResult> ProbeAsync(IEnumerable<string> arguments, CancellationToken cancellationToken) =>
        RunAsync(IsProbeAvailable ? ProbePath(ExecutablePath!) : null, arguments, cancellationToken);
    public Task<FFmpegResult> ExecuteAsync(IEnumerable<string> arguments, CancellationToken cancellationToken) =>
        RunAsync(IsAvailable ? ExecutablePath : null, arguments, cancellationToken);

    private static async Task<FFmpegResult> RunAsync(string? executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        if (executable is null) return new(false, string.Empty, "FFmpeg executable not found.", -1);
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        Task<(string Text, bool Truncated)>? stdout = null, stderr = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            process.Start();
            stdout = DrainAsync(process.StandardOutput); stderr = DrainAsync(process.StandardError);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false); var error = await stderr.ConfigureAwait(false);
            return new(process.ExitCode == 0 && !output.Truncated, output.Text, error.Text, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
            return new(false, string.Empty, "Process was canceled.", -2);
        }
        catch (Exception)
        {
            await StopAsync().ConfigureAwait(false);
            return new(false, string.Empty, "The local media process could not run.", -3);
        }
        async Task StopAsync()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
            if (stdout is not null) await stdout.ConfigureAwait(false);
            if (stderr is not null) await stderr.ConfigureAwait(false);
        }
    }
    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader)
    {
        const int limit = 1048576; var builder = new StringBuilder(); var buffer = new char[8192]; bool truncated = false; int n;
        while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            int keep = Math.Min(n, limit - builder.Length); if (keep < n) truncated = true;
            builder.Append(buffer, 0, keep);
        }
        return (builder.ToString(), truncated);
    }
}
