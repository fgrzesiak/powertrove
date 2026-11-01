using System.Security.Cryptography;
using System.Text.Json;
using FileKit.Imaging;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;
using PowerTrove.Abstractions;
using PowerTrove.Application.Packs;
using PowerTrove.Platform.Windows.Deployment;
using PowerTrove.Platform.Windows.Packs;

namespace PowerTrove.Desktop.AcceptanceTests;

public sealed class InstalledMsixFactAttribute : FactAttribute
{
    public InstalledMsixFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("POWERTROVE_MSIX_PACK_PROBE") != "1")
            Skip = "Not Run: requires the explicitly dispatched disposable installed-MSIX probe.";
    }
}

/// <summary>Real signed-channel/installed-worker probe. Does not substitute for UI/Office/lifecycle acceptance.</summary>
public sealed class InstalledPackProbe
{
    [InstalledMsixFact]
    public async Task OfficialImagesPack_DownloadsConvertsRejectsTamperingAndReinstalls()
    {
        Assert.True(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") == "fgrzesiak/powertrove");
        Assert.True(global::Windows.ApplicationModel.Package.Current.Id.Name == "PowerTrove.LocalTest");
        var distribution = WindowsAppDistribution.Current;
        Assert.True(distribution.IsPackaged);
        string app = global::Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
        var version = System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(app, "PowerTrove.PackHost.dll")).Version!;
        string owned = Path.Combine(distribution.UserRoot, "installed-pack-probe");
        Assert.False(Directory.Exists(owned));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        await using var supervisor = new PackHostSupervisor(new WindowsPackWorkerProcessFactory(Path.Combine(app, "PowerTrove.PackHost.exe")), distribution.UserRoot);
        var installer = new PackInstaller(distribution.UserRoot, version, supervisor.StopPackAsync);
        // This probe is permitted only immediately after the script's fresh-package guard/install.
        Assert.Empty(await installer.GetInstalledAsync(token));
        Directory.CreateDirectory(owned);
        var checks = new Dictionary<string, string>();
        string stage = "catalog";
        try
        {
            using var channel = PackChannelClient.CreateDefault(installer, distribution.UserRoot, version);
            var catalog = await channel.RefreshOnUserRequestAsync(token);
            Assert.True(catalog.Status == PackChannelStatus.Ready);
            Assert.Empty(await installer.GetInstalledAsync(token));
            checks["catalog-does-not-install"] = "Pass";
            stage = "install";
            var offer = await channel.PrepareOnUserRequestAsync("powertrove.images", token);
            Assert.True(offer.Status == PackChannelStatus.Ready && offer.Offer is not null);
            var result = await channel.InstallOnUserRequestAsync("powertrove.images", token: token, approvedOffer: offer.Offer);
            Assert.True(result.Status == PackChannelStatus.Installed && result.Installed is not null);
            checks["official-download-signature-manifest"] = "Pass";
            var installed = result.Installed!;
            string input = Path.Combine(owned, "generated.png");
            await using (var output = File.Create(input)) PngEncoder.Encode(new RasterImage(2, 2, Enumerable.Repeat((byte)255, 16).ToArray()), output, ct: token);
            byte[] originalHash = SHA256.HashData(await File.ReadAllBytesAsync(input, token));
            async Task ConvertAsync()
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 0, null, now), ContextStatus.Available,
                    new FileSelection([new SelectedFile(input, "generated.png", ".png", "image/png", new FileInfo(input).Length, true, true, null)]),
                    now, now.AddMinutes(2), "generated-acceptance", null);
                var group = new PackWorkerGroup(installed.WorkerGroupId + "#" + Guid.NewGuid().ToString("N"), [installed.Directory],
                    [new(installed.Manifest.Id, installed.Manifest.Version)], [installed.ManifestSha256]);
                var preview = await supervisor.PreviewAsync(group, "powertrove.image.convert-webp", snapshot, new EmptyParameters(), token);
                Assert.False(preview.IsBlocked);
                var receipt = await supervisor.ExecuteAsync(group, "powertrove.image.convert-webp", snapshot, new EmptyParameters(), null, token);
                Assert.True(receipt.IsSuccess && receipt.OutputPaths.Count == 1);
                var image = await ImageProbe.ProbeFileAsync(receipt.OutputPaths[0], token);
                Assert.True(image.Format == ImageFormat.WebP && image.Width == 2 && image.Height == 2);
                Assert.True(SHA256.HashData(await File.ReadAllBytesAsync(input, token)).SequenceEqual(originalHash));
                Assert.True(receipt.OutputPaths[0] != input);
            }
            stage = "conversion";
            await ConvertAsync();
            checks["packaged-worker-conversion-source-preserved"] = "Pass";
            // Fresh worker, no catalog refresh or download: this proves local reuse, not disconnected networking.
            stage = "worker-stop";
            await supervisor.StopPackAsync(installed.Manifest.Id, token);
            stage = "local-reuse";
            await ConvertAsync();
            checks["local-reuse-no-catalog-request"] = "Pass";
            string manifestPath = Path.Combine(installed.Directory, "manifest.json");
            stage = "tamper-rejection";
            byte[] manifest = await File.ReadAllBytesAsync(manifestPath, token);
            try
            {
                byte[] damaged = (byte[])manifest.Clone(); damaged[^1] ^= 1;
                await File.WriteAllBytesAsync(manifestPath, damaged, token);
                Assert.Empty(await installer.GetInstalledAsync(token));
                checks["tampered-installed-manifest-rejected"] = "Pass";
            }
            finally { await File.WriteAllBytesAsync(manifestPath, manifest, CancellationToken.None); }
            stage = "remove-reinstall";
            await installer.UninstallAsync(installed.Manifest.Id, token);
            Assert.Empty(await installer.GetInstalledAsync(token));
            result = await channel.InstallOnUserRequestAsync("powertrove.images", token: token, approvedOffer: offer.Offer);
            Assert.True(result.Status == PackChannelStatus.Installed);
            installed = result.Installed!;
            await ConvertAsync();
            checks["remove-reinstall-conversion"] = "Pass";
            await installer.UninstallAsync(installed.Manifest.Id, token);
            var packageVersion = global::Windows.ApplicationModel.Package.Current.Id.Version;
            var report = new { schemaVersion = 1, kind = "packaged-context-debug-probe", result = "Pass",
                packageVersion = $"{packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}.{packageVersion.Revision}",
                pack = new { installed.Manifest.Id, installed.Manifest.Version, installed.ManifestSha256 }, checks,
                uiOfficeNetworkDisconnectAndLifecycle = "Not Run" };
            string reportPath = Environment.GetEnvironmentVariable("POWERTROVE_MSIX_PROBE_REPORT") ?? throw new InvalidOperationException();
            string runnerTemp = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? throw new InvalidOperationException()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert.StartsWith(runnerTemp, Path.GetFullPath(reportPath), StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report), token);
        }
        catch (Exception error)
        {
            string? reportPath = Environment.GetEnvironmentVariable("POWERTROVE_MSIX_PROBE_REPORT");
            string? runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
            if (reportPath is not null && runnerTemp is not null && Path.GetFullPath(reportPath).StartsWith(
                Path.GetFullPath(runnerTemp).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { schemaVersion = 1,
                    kind = "packaged-context-debug-probe", result = "Fail", stage, errorType = error.GetType().Name,
                    hresult = error.HResult,
                    components = new System.Diagnostics.StackTrace(error).GetFrames().Select(f => f.GetMethod())
                        .Where(m => m?.DeclaringType?.FullName?.StartsWith("PowerTrove.", StringComparison.Ordinal) == true)
                        .Select(m => m!.DeclaringType!.FullName + "." + m.Name).Take(8).ToArray(), checks,
                    uiOfficeNetworkDisconnectAndLifecycle = "Not Run" }), CancellationToken.None);
            throw;
        }
        finally
        {
            await supervisor.StopPackAsync("powertrove.images", CancellationToken.None);
            // The outer script removes only its newly installed package. Never touch another installation.
            Directory.Delete(owned, recursive: true);
        }
    }
}
