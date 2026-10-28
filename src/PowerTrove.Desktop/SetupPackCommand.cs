using System;
using System.IO;
using System.Threading;
using PowerTrove.Application.Packs;

namespace PowerTrove.Desktop;

/// <summary>
/// Setup's last step on a new installation: installs the selected packs without a window, host election or palette.
/// Exit code 0 means every selected pack is installed; otherwise the rest stays selected for the app's next start.
/// </summary>
internal static class SetupPackCommand
{
    public const string Argument = "--install-setup-packs";

    public static int Run()
    {
        string selection = Path.Combine(AppContext.BaseDirectory, "setup-packs.json");
        try
        {
            var packs = BuiltInPackMigration.ReadSetupSelection(selection);
            if (packs.Count == 0) { BuiltInPackMigration.WriteSetupSelection(selection, []); return 0; }
            string userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerTrove");
            // No worker runs yet; the store lock serializes this with any app instance.
            var installer = new PackInstaller(userRoot, PackDistributionConfiguration.HostVersion, (_, _) => System.Threading.Tasks.Task.CompletedTask);
            using var channel = PackChannelClient.CreateDefault(installer, userRoot, PackDistributionConfiguration.HostVersion);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            channel.LoadCachedAsync(timeout.Token).GetAwaiter().GetResult();
            var remaining = new SetupPackInstallation(channel).InstallAsync(packs, timeout.Token).GetAwaiter().GetResult();
            BuiltInPackMigration.WriteSetupSelection(selection, remaining);
            return remaining.Count == 0 ? 0 : 1;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidDataException
            or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or ArgumentException)
        { return 1; }
    }
}
