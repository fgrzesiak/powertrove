using System.Diagnostics;
using PowerTrove.Application.Updates;

namespace PowerTrove.Platform.Windows.Shell;

/// <summary>Opens a validated release page in the user's browser; never executes a release asset.</summary>
public sealed class ReleasePageLauncher : IReleasePageLauncher
{
    public Task<bool> OpenAsync(Uri page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReleaseSource.TryValidatePage(page, out var canonical)) return Task.FromResult(false);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(canonical!.AbsoluteUri) { UseShellExecute = true, Verb = "open" });
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { return Task.FromResult(false); }
    }
}
