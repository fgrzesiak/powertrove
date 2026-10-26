using System.Collections.Generic;
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
public interface IFFmpegService
{
    bool IsAvailable { get; }
    bool IsProbeAvailable => false;
    Task<FFmpegResult> ProbeAsync(IEnumerable<string> arguments, CancellationToken cancellationToken) =>
        Task.FromResult(new FFmpegResult(false, string.Empty, string.Empty, -1));
    string? ExecutablePath { get; }
    Task<string?> GetVersionAsync(CancellationToken cancellationToken);
    Task<FFmpegResult> ExecuteAsync(IEnumerable<string> arguments, CancellationToken cancellationToken);
}

public sealed record FFmpegResult(bool Success, string Output, string Error, int ExitCode);
