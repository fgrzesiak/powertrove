using PowerTrove.Abstractions.Manifest;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Application.Configuration;
using PowerTrove.Application.Packs;
using PowerTrove.Application.ViewModels;
using PowerTrove.Core;
using PowerTrove.Core.Catalog;
using PowerTrove.Core.Search;

namespace PowerTrove.Application.Tests;

public sealed class ContextualPacksTests
{
    [Theory]
    [InlineData(".png", "image", "Image", "powertrove.images")]
    [InlineData(".pdf", "pdf", "PDF", "powertrove.pdf")]
    [InlineData(".mp4", "video", "Video", "powertrove.media")]
    [InlineData(".txt", "text", "Text", "powertrove.data")]
    public async Task EveryMatchingPackGetsOneCard_EvenWithInstalledGenericActions(string extension, string family, string title, string pack)
    {
        using var temp = new TempDirectory();
        var registry = new ActionRegistry();
        var missing = Missing(pack, extension, family, title);
        registry.RegisterPack(pack, [missing, Missing(pack, extension, family, title, "second")]);
        registry.Register(new LocalAction("powertrove.files.generic", "Generic file tool"));
        var service = new Management(registry, missing);
        var snapshot = Snapshots.ForPaths(temp.Write("input" + extension, "generated"));
        var palette = new PaletteViewModel(); palette.ContextPacks.Configure(service);
        palette.Initialize(snapshot, registry, new ActionSearchEngine(registry)); await palette.SearchTask;
        var card = Assert.Single(palette.ContextPacks.Suggestions);
        Assert.Equal(pack, card.Id); Assert.True(card.CanDownload);
        Assert.Contains("2", card.Description);
        Assert.Contains(palette.FilteredActions, r => r.Action.Descriptor.Id == "powertrove.files.generic");
        Assert.Equal(0, service.Prepares); Assert.Equal(0, service.Installs);
        palette.SearchText = "unrelated-zzzzzz"; await palette.SearchTask;
        Assert.Empty(palette.ContextPacks.Suggestions);
        palette.SearchText = title; await palette.SearchTask;
        Assert.Single(palette.ContextPacks.Suggestions);
    }

    [Fact]
    public async Task OneClickInstallsAndPublishesActions_WithoutExecutingOrChangingQueryOrSelection()
    {
        using var temp = new TempDirectory();
        var registry = new ActionRegistry(); var missing = Missing(); registry.Register(missing);
        var inner = new Management(registry, missing);
        using var service = new CoordinatedPackManagement(inner);
        var palette = new PaletteViewModel(); palette.ContextPacks.Configure(service);
        using var binding = new PackRegistryBinding(registry, palette, action => { action(); return true; });
        var snapshot = Snapshots.ForPaths(temp.Write("input.png", "original"));
        palette.Initialize(snapshot, registry, new ActionSearchEngine(registry)); await palette.SearchTask;
        palette.SearchText = "Image"; await palette.SearchTask;
        var card = Assert.Single(palette.ContextPacks.Suggestions);
        await palette.ContextPacks.InstallAsync(card); await palette.SearchTask;
        Assert.Equal(1, inner.Prepares); Assert.Equal(1, inner.Installs);
        Assert.Empty(palette.ContextPacks.Suggestions);
        Assert.Contains(palette.FilteredActions, r => ReferenceEquals(r.Action, inner.Installed));
        Assert.Same(snapshot, palette.CurrentSnapshot); Assert.Equal("Image", palette.SearchText);
        Assert.Equal(0, inner.Installed.Executions); Assert.Equal("original", File.ReadAllText(temp.File("input.png")));
    }

    [Fact]
    public async Task SettingsInstallationRefreshesOpenPalette_AndRemovalRestoresDiscovery()
    {
        using var temp = new TempDirectory();
        var registry = new ActionRegistry(); var missing = Missing(); registry.Register(missing);
        var inner = new Management(registry, missing);
        using var service = new CoordinatedPackManagement(inner);
        var palette = new PaletteViewModel(); palette.ContextPacks.Configure(service);
        using var binding = new PackRegistryBinding(registry, palette, action => { action(); return true; });
        palette.Initialize(Snapshots.ForPaths(temp.Write("input.png", "generated")), registry, new ActionSearchEngine(registry));
        await palette.SearchTask;
        using var settings = new ActionPacksViewModel(service, new SettingsService(temp.File("settings.json")));
        await settings.OpenAsync();
        await settings.InstallAsync(inner.Offer); await palette.SearchTask;
        Assert.Empty(palette.ContextPacks.Suggestions); Assert.Same(inner.Installed, Assert.Single(palette.FilteredActions).Action);
        await settings.UninstallAsync(missing.PackId); await palette.SearchTask;
        Assert.Single(palette.ContextPacks.Suggestions);
        Assert.IsType<CatalogAction>(Assert.Single(palette.FilteredActions).Action);
    }

    [Fact]
    public async Task ChangedOfferRequiresAnotherClick_WithoutSilentlyApprovingLargerOrDifferentPackage()
    {
        using var temp = new TempDirectory(); var registry = new ActionRegistry(); var missing = Missing();
        var service = new Management(registry, missing); registry.Register(missing);
        var palette = new PaletteViewModel(); palette.ContextPacks.Configure(service);
        palette.Initialize(Snapshots.ForPaths(temp.Write("input.png", "generated")), registry, new ActionSearchEngine(registry)); await palette.SearchTask;
        service.Offer = service.Offer with { Version = "1.1.0", DownloadBytes = 2000, ManifestFingerprint = "new" };
        await palette.ContextPacks.InstallAsync(Assert.Single(palette.ContextPacks.Suggestions));
        Assert.Equal(0, service.Installs);
        var revised = Assert.Single(palette.ContextPacks.Suggestions); Assert.Contains("2", revised.DownloadText);
        await palette.ContextPacks.InstallAsync(revised); Assert.Equal(1, service.Installs);
    }

    [Fact]
    public async Task HiddenDismissedOrUnavailableContextsDoNotAdvertiseDownloads()
    {
        using var temp = new TempDirectory(); var registry = new ActionRegistry(); var missing = Missing(); registry.Register(missing);
        var service = new Management(registry, missing);
        var settings = new SettingsService(temp.File("settings.json"));
        var palette = new PaletteViewModel(); palette.ContextPacks.Configure(service);
        var snapshot = Snapshots.ForPaths(temp.Write("input.png", "generated"));
        palette.Initialize(snapshot, registry, new ActionSearchEngine(registry), settings); await palette.SearchTask;
        settings.SetShowNotInstalledActions(false); palette.RefreshPackVisibility(); Assert.Empty(palette.ContextPacks.Suggestions);
        settings.SetShowNotInstalledActions(true); palette.RefreshPackVisibility();
        palette.ContextPacks.Dismiss(missing.PackId); await palette.RefreshActionsAsync(); Assert.Empty(palette.ContextPacks.Suggestions);
        palette.Initialize(snapshot with { Status = ContextStatus.Unsupported, Content = new NoContent(), InvocationId = Guid.NewGuid() }, registry, new ActionSearchEngine(registry));
        await palette.SearchTask; Assert.Empty(palette.ContextPacks.Suggestions); Assert.Equal(0, service.Prepares);
    }

    [Fact]
    public async Task SharedProgressAndCancel_DeduplicateInstallAcrossEntryPoints()
    {
        var registry = new ActionRegistry(); var missing = Missing(); var inner = new Management(registry, missing) { Pending = new() };
        using var service = new CoordinatedPackManagement(inner);
        int changes = 0; service.Changed += (_, _) => changes++;
        var first = service.InstallAsync(inner.Offer, null, default);
        var second = service.InstallAsync(inner.Offer, null, default);
        Assert.Equal(1, inner.Installs); Assert.NotNull(service.Activity);
        Assert.Equal(PackChannelStatus.Busy, await service.UninstallAsync(missing.PackId, default));
        inner.Progress!.Report(new(50, 100)); Assert.Equal(50, service.Activity!.CompletedBytes);
        inner.Pending.SetResult(PackChannelStatus.Installed);
        Assert.Equal(PackChannelStatus.Installed, await first); Assert.Equal(PackChannelStatus.Installed, await second);
        Assert.Null(service.Activity); Assert.Equal(1, changes);
    }

    [Fact]
    public async Task RegistryRefreshesAreCoalesced_AndQueuedCallbacksAreRetiredOnClose()
    {
        var registry = new ActionRegistry(); var queue = new Queue<Action>(); var palette = new PaletteViewModel();
        var binding = new PackRegistryBinding(registry, palette, action => { queue.Enqueue(action); return true; });
        registry.Register(new LocalAction("powertrove.a.one", "one")); registry.Register(new LocalAction("powertrove.a.two", "two"));
        Assert.Single(queue); binding.Dispose(); queue.Dequeue()(); await palette.SearchTask;
        Assert.Equal(0, palette.SelectionRevision);
        registry.Register(new LocalAction("powertrove.a.three", "three")); Assert.Empty(queue);
    }

    private static CatalogAction Missing(string id = "powertrove.images", string extension = ".png", string family = "image", string title = "Image tool", string suffix = "first") => new(new CatalogEntry(
        new ManifestAction("powertrove." + family + "." + suffix, title, ["files"], Selection: ManifestSelection.FromRequirements(SelectionRequirements.Any(family)),
            Extensions: [extension], ChangesFiles: true), new ActionPackInfo(id, title + " pack", "1.0.0", "PowerTrove tests"), PackInstallState.NotInstalled, 1000, "fingerprint"));
    private sealed class Management(ActionRegistry registry, CatalogAction missing) : IPackManagementService
    {
        public int Prepares, Installs; public bool Present;
        public PackInstallOffer Offer = new(missing.PackId, missing.PackVersion, missing.PackName, missing.Publisher, missing.DownloadBytes, missing.ManifestFingerprint!);
        public LocalAction Installed { get; } = new(missing.Descriptor.Id, missing.Descriptor.Title);
        public TaskCompletionSource<PackChannelStatus>? Pending;
        public IProgress<PackDownloadProgress>? Progress;
        public Task<PackOfferResult> PrepareAsync(string id, CancellationToken token) { Prepares++; return Task.FromResult(new PackOfferResult(PackChannelStatus.Ready, Offer)); }
        public async Task<PackChannelStatus> InstallAsync(PackInstallOffer offer, IProgress<PackDownloadProgress>? progress, CancellationToken token)
        {
            Installs++; Progress = progress;
            var status = Pending is null ? PackChannelStatus.Installed : await Pending.Task.WaitAsync(token);
            if (status == PackChannelStatus.Installed) { Present = true; registry.RegisterPack(offer.Id, [Installed]); }
            return status;
        }
        public Task<PackPageSnapshot> OpenOnUserRequestAsync(CancellationToken token) => GetLocalAsync(token);
        public Task<PackPageSnapshot> GetLocalAsync(CancellationToken token) => Task.FromResult(new PackPageSnapshot(PackChannelStatus.Ready,
            [new PackListItem(Offer.Id, Offer.Name, Offer.Publisher, Present ? Offer.Version : null, Offer.Version, Offer.DownloadBytes, 1,
                Present ? PackInstallState.Installed : PackInstallState.NotInstalled, !Present, Present, false)]));
        public Task<PackChannelStatus> UninstallAsync(string id, CancellationToken token)
        { Present = false; registry.RegisterPack(id, [missing]); return Task.FromResult(PackChannelStatus.Ready); }
        public Task<PackChannelStatus> InstallFileAsync(string path, CancellationToken token) => InstallAsync(Offer, null, token);
    }
    private sealed class LocalAction(string id, string title) : IAction
    {
        public int Executions;
        public ActionDescriptor Descriptor { get; } = new(id, title, "Generated", new HashSet<ContentKind> { ContentKind.Files }, [title], false, false);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken token) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken token) => new(new ActionPreview(false, null, [], []));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken token)
        { Executions++; return Task.FromResult(new ActionReceipt(true, false, [], [], false)); }
    }
}
