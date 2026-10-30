using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileKit.Media;

namespace PowerTrove.Pack.Media;

/// <summary>
/// Provides secure invocation of local FFmpeg processes.
/// Note on Redistribution: FFmpeg is licensed under the LGPL or GPL (depending on compilation flags).
/// If PowerTrove ever bundles or distributes FFmpeg binaries, it must comply with these licenses
/// (e.g., providing source code, dynamic linking for LGPL). PowerTrove never bundles FFmpeg: the user
/// installs it, either themselves or through the guided winget installation in Settings.
/// </summary>
public class FFmpegService : IFFmpegService
{
    private readonly string? _customPath;
    private readonly Func<string?> _locate;
    private readonly long _lookupIntervalMs;
    private volatile string? _executablePath;
    private string? _version;
    private bool? _isAvailable;
    private long _nextLookup;

    public FFmpegService(string? customPath = null) : this(customPath, FFmpegLocator.Find, 5000) { }

    internal FFmpegService(string? customPath, Func<string?> locate, long lookupIntervalMs)
    {
        _lookupIntervalMs = lookupIntervalMs;
        _customPath = customPath;
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
        _executablePath = customPath ?? locate();
    }

    public bool IsAvailable
    {
        get
        {
            if (_isAvailable == true) return true;
            // A missing FFmpeg is looked up again (at most every few seconds), so an installation made while
            // the pack worker runs is picked up without restarting it.
            long now = Environment.TickCount64;
            if (_isAvailable == false && now < Interlocked.Read(ref _nextLookup)) return false;
            if (_isAvailable.HasValue && _customPath is null) _executablePath = _locate();
            bool available = !string.IsNullOrEmpty(_executablePath) && File.Exists(_executablePath);
            Interlocked.Exchange(ref _nextLookup, now + _lookupIntervalMs);
            _isAvailable = available;
            return available;
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
