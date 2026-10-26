using PowerTrove.Core.Catalog;

namespace PowerTrove.Application.Packs;

public sealed record PackListItem(string Id, string Name, string Publisher, string? InstalledVersion, string AvailableVersion,
    long SizeBytes, int ActionCount, PackInstallState State, bool CanDownload, bool CanUninstall, bool IsBuiltIn);
public sealed record PackPageSnapshot(PackChannelStatus Status, IReadOnlyList<PackListItem> Packs);
public interface IPackManagementService
{
    Task<PackPageSnapshot> OpenOnUserRequestAsync(CancellationToken token);
    Task<PackPageSnapshot> GetLocalAsync(CancellationToken token);
    Task<PackOfferResult> PrepareAsync(string id, CancellationToken token);
    Task<PackChannelStatus> InstallAsync(PackInstallOffer offer, IProgress<PackDownloadProgress>? progress, CancellationToken token);
    Task<PackChannelStatus> InstallFileAsync(string path, CancellationToken token);
    Task<PackChannelStatus> UninstallAsync(string id, CancellationToken token);
}

public sealed class PackManagementService(PackInstaller installer, PackChannelClient channel, InstalledPackRuntime runtime,
    IReadOnlyList<IActionPack> builtIns, IActionCatalog bundledCatalog) : IPackManagementService
{
    public async Task<PackPageSnapshot> OpenOnUserRequestAsync(CancellationToken token)
    {
        var result = await channel.RefreshOnUserRequestAsync(token).ConfigureAwait(false);
        return new(result.Status, await ListAsync(token).ConfigureAwait(false));
    }
    public async Task<PackPageSnapshot> GetLocalAsync(CancellationToken token) => new(PackChannelStatus.Ready, await ListAsync(token).ConfigureAwait(false));
    public Task<PackOfferResult> PrepareAsync(string id, CancellationToken token) => channel.PrepareOnUserRequestAsync(id, token);
    public async Task<PackChannelStatus> InstallAsync(PackInstallOffer offer, IProgress<PackDownloadProgress>? progress, CancellationToken token)
    {
        var result = await channel.InstallOnUserRequestAsync(offer.Id, progress, token, offer).ConfigureAwait(false);
        if (result.Status == PackChannelStatus.Installed) await runtime.ReloadAsync().ConfigureAwait(false);
        return result.Status;
    }
    public async Task<PackChannelStatus> InstallFileAsync(string path, CancellationToken token)
    {
        await installer.InstallAsync(path, token).ConfigureAwait(false);
        await runtime.ReloadAsync().ConfigureAwait(false);
        return PackChannelStatus.Installed;
    }
    public async Task<PackChannelStatus> UninstallAsync(string id, CancellationToken token)
    {
        await installer.UninstallAsync(id, token).ConfigureAwait(false);
        await runtime.ReloadAsync().ConfigureAwait(false);
        return PackChannelStatus.Ready;
    }
    private async Task<IReadOnlyList<PackListItem>> ListAsync(CancellationToken token)
    {
        IReadOnlyList<InstalledPack> localPacks;
        try { localPacks = await installer.GetInstalledAsync(token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { localPacks = []; }
        var installed = localPacks.ToDictionary(p => p.Manifest.Id, StringComparer.Ordinal);
        var builtIn = builtIns.ToDictionary(p => p.Info.Id, StringComparer.Ordinal);
        var localCatalog = bundledCatalog.Entries.GroupBy(e => e.Pack.Id).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var available = channel.CachedPacks.ToDictionary(p => p.Manifest.Id, StringComparer.Ordinal);
        var ids = installed.Keys.Concat(builtIn.Keys).Concat(localCatalog.Keys).Concat(available.Keys).Distinct(StringComparer.Ordinal);
        var rows = new List<PackListItem>();
        foreach (string id in ids)
        {
            installed.TryGetValue(id, out var local); builtIn.TryGetValue(id, out var bundled);
            available.TryGetValue(id, out var remote); localCatalog.TryGetValue(id, out var catalog);
            var metadata = remote?.Manifest ?? local?.Manifest;
            var info = bundled?.Info ?? catalog?.FirstOrDefault()?.Pack;
            string name = local?.Manifest.DisplayName ?? bundled?.Info.DisplayName ?? metadata?.DisplayName ?? info!.DisplayName;
            string publisher = metadata?.Publisher ?? info!.Publisher;
            string? version = local?.Manifest.Version ?? bundled?.Info.Version;
            string latest = remote?.Manifest.Version ?? local?.Manifest.Version ?? info!.Version;
            bool compatible = remote?.IsCompatible ?? (catalog?.Any(e => e.State == PackInstallState.Incompatible) != true);
            var state = version is not null ? compatible && Version.Parse(latest) > Version.Parse(version) ? PackInstallState.UpdateAvailable : PackInstallState.Installed
                : compatible ? PackInstallState.NotInstalled : PackInstallState.Incompatible;
            rows.Add(new PackListItem(id, name, publisher, version, latest,
                remote?.DownloadBytes ?? local?.Manifest.Payload?.SizeBytes ?? catalog?.FirstOrDefault()?.DownloadBytes ?? 0,
                metadata?.Actions.Count ?? bundled?.Actions.Count ?? catalog?.Length ?? 0, state,
                remote is { IsCompatible: true } && state is PackInstallState.NotInstalled or PackInstallState.UpdateAvailable,
                local is not null, bundled is not null));
        }
        return rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
    }
}
