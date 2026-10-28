using System.Diagnostics;
using PowerTrove.Application.Updates;

namespace PowerTrove.Platform.Windows.Shell;

public sealed class ReleaseInstallerLauncher : IReleaseInstallerLauncher
{
    public Task<bool> StartAsync(string installerPath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Preserve download origin and normal Windows shell/SmartScreen checks, including unsigned alpha warnings.
        File.WriteAllText(installerPath + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://github.com/" +
            ReleaseSource.Repository + "\r\n");
        var start = new ProcessStartInfo(installerPath) { UseShellExecute = true };
        // Upgrade the running folder, including ZIP users explicitly opting into the per-user installer.
        start.ArgumentList.Add("/DIR=" + Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        start.ArgumentList.Add("/SILENT");
        start.ArgumentList.Add("/NORESTART");
        start.ArgumentList.Add("/POWERTROVEUPDATE");
        start.ArgumentList.Add("/NOFORCECLOSEAPPLICATIONS");
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(start);
        return process is not null;
    }, cancellationToken);
}
