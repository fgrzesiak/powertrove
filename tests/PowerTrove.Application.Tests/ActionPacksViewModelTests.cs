using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Application.Configuration;
using PowerTrove.Application.Packs;
using PowerTrove.Application.ViewModels;
using PowerTrove.Core;
using PowerTrove.Core.Catalog;
using PowerTrove.Core.Search;

namespace PowerTrove.Application.Tests;

public sealed class ActionPacksViewModelTests
{
    [Fact]
    public async Task ProductionManagementKeepsBundledAndDiscoveryRowsWhenHostingIsAbsent()
    {
        using var temp = new TempDirectory();
        var installer = new PackInstaller(temp.File("user"), new Version(0, 1, 0), (_, _) => Task.CompletedTask);
        using var channel = new PackChannelClient(new PowerTrove.Application.Updates.OwnerChannelConfiguration(),
            new HttpClientHandler(), installer, temp.File("user"), new Version(0, 1, 0));
        var factory = new NoWorkerFactory();
        await using var supervisor = new PackHostSupervisor(factory, temp.File("user"));
        IActionPack[] builtIns = [new GeneratedPack()];
        var catalog = new TestCatalog([new CatalogEntry(new ManifestAction("powertrove.optional.copy", "Generated optional", ["files"]),
            new ActionPackInfo("powertrove.optional", "Generated optional tools", "0.1.0", "PowerTrove tests"), PackInstallState.NotInstalled, 1000)]);
        var runtime = new InstalledPackRuntime(installer, supervisor, new ActionRegistry(), builtIns, catalog);
        var service = new PackManagementService(installer, channel, runtime, builtIns, catalog);
        var snapshot = await service.OpenOnUserRequestAsync(default);
        Assert.Equal(PackChannelStatus.NotConfigured, snapshot.Status);
        var bundled = snapshot.Packs.Single(p => p.Id == "powertrove.fixture");
        Assert.Equal(PackInstallState.Installed, bundled.State); Assert.True(bundled.IsBuiltIn); Assert.False(bundled.CanUninstall);
        var missing = snapshot.Packs.Single(p => p.Id == "powertrove.optional");
        Assert.Equal(PackInstallState.NotInstalled, missing.State); Assert.False(missing.CanDownload);
        Assert.Equal(0, factory.Starts);
        using var vm = new ActionPacksViewModel(service, new SettingsService(temp.File("settings.json")));
        await vm.OpenAsync();
        Assert.Contains(vm.Rows, row => row.Id == "powertrove.fixture");
    }

    [Fact]
    public async Task ExplicitOpenListsEveryStateAndEnablesOnlyApplicableCommands()
    {
        using var temp = new TempDirectory();
        var service = new FakeManagement();
        using var vm = new ActionPacksViewModel(service, new SettingsService(temp.File("settings.json")));
        Assert.Equal(0, service.Opens);
        Assert.Empty(vm.Rows);
        await vm.OpenAsync();
        Assert.Equal(1, service.Opens);
        Assert.Equal(4, vm.Rows.Count);
        Assert.DoesNotContain(vm.Rows, row => row.Id == "powertrove.recipes");
        Assert.False(vm.Rows.Single(r => r.Id == "powertrove.installed").CanDownload);
        Assert.True(vm.Rows.Single(r => r.Id == "powertrove.missing").CanDownload);
        Assert.Equal("Update", vm.Rows.Single(r => r.Id == "powertrove.update").PrimaryText);
        Assert.False(vm.Rows.Single(r => r.Id == "powertrove.incompatible").CanDownload);
        Assert.False(vm.Rows.Single(r => r.Id == "powertrove.incompatible").CanUninstall);
        await vm.InstallAsync((await vm.PrepareInstallAsync("powertrove.missing"))!);
        await vm.InstallAsync((await vm.PrepareInstallAsync("powertrove.update"))!);
        await vm.UninstallAsync("powertrove.installed");
        await vm.InstallFileAsync(temp.File("generated.cbpack"));
        Assert.Equal(new[] { "install:powertrove.missing", "install:powertrove.update", "remove:powertrove.installed", "file" }, service.Commands);
        Assert.Equal(4, service.LocalLoads);
        Assert.DoesNotContain(vm.Rows, row => row.Id == "powertrove.recipes");
        await vm.InstallAsync(new PackInstallOffer("powertrove.incompatible", "99.0.0", "Generated incompatible", "PowerTrove tests", 1000, "generated"));
        await vm.UninstallAsync("powertrove.missing");
        Assert.Equal(4, service.Commands.Count);
    }

    [Fact]
    public async Task CancelAndDisposeStopActiveWorkWithoutPublishingPrivateExceptions()
    {
        using var temp = new TempDirectory();
        var service = new FakeManagement();
        using var vm = new ActionPacksViewModel(service, new SettingsService(temp.File("settings.json")));
        service.HoldOpen = true;
        var open = vm.OpenAsync();
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanOperate);
        vm.Cancel(); await open;
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanOperate);
        var again = vm.OpenAsync(); vm.Dispose(); await again;
        Assert.False(vm.CanOperate);
        Assert.Equal(2, service.Canceled);
    }

    [Theory]
    [InlineData(PackChannelStatus.Offline)]
    [InlineData(PackChannelStatus.TimedOut)]
    [InlineData(PackChannelStatus.InvalidData)]
    [InlineData(PackChannelStatus.NotConfigured)]
    public async Task CatalogFailureStillShowsInstalledTools(PackChannelStatus status)
    {
        using var temp = new TempDirectory();
        var service = new FakeManagement { Status = status };
        using var vm = new ActionPacksViewModel(service, new SettingsService(temp.File("settings.json")));
        await vm.OpenAsync();
        Assert.Contains(vm.Rows, r => r.Item.State == PackInstallState.Installed);
        Assert.NotEmpty(vm.StatusMessage);
        Assert.DoesNotContain("private", vm.StatusMessage);
    }

    [Fact]
    public async Task PreferenceDefaultsOnPersistsAndImmediatelyFiltersRealCatalogSearch()
    {
        using var temp = new TempDirectory();
        string path = temp.File("settings.json");
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(UserSettings.CreateDefault()))!.AsObject();
        legacy.Remove(nameof(UserSettings.ShowNotInstalledActions)); File.WriteAllText(path, legacy.ToJsonString());
        var settings = new SettingsService(path);
        Assert.True(settings.Settings.ShowNotInstalledActions);
        var registry = new ActionRegistry(); registry.Register(new InstalledAction());
        registry.RegisterCatalog(new TestCatalog([new CatalogEntry(new ManifestAction("powertrove.optional.copy", "Generated fixture optional", ["files"],
            Selection: new ManifestSelection(["text"], 1, null, null, null, false), Extensions: [".txt"], ChangesFiles: true),
            new ActionPackInfo("powertrove.optional", "Generated optional tools", "0.1.0", "PowerTrove tests"), PackInstallState.NotInstalled, 1000)]));
        var palette = new PaletteViewModel();
        palette.Initialize(Snapshots.ForPaths(temp.Write("input.txt", "generated")), registry, new ActionSearchEngine(registry), settings);
        await palette.SearchTask;
        Assert.Contains(palette.FilteredActions, r => r.InstallState == PackInstallState.NotInstalled);
        using var vm = new ActionPacksViewModel(new FakeManagement(), settings, palette.RefreshPackVisibility);
        vm.ShowNotInstalledActions = false;
        Assert.DoesNotContain(palette.FilteredActions, r => r.InstallState == PackInstallState.NotInstalled);
        Assert.Contains(palette.FilteredActions, r => r.Action.Descriptor.Id == "powertrove.fixture.installed");
        palette.IncludeUnavailableActions = true; palette.SearchText = "fixture"; await palette.SearchTask;
        Assert.DoesNotContain(palette.FilteredActions, r => r.InstallState == PackInstallState.NotInstalled);
        Assert.False(new SettingsService(path).Settings.ShowNotInstalledActions);
        vm.ShowNotInstalledActions = true;
        Assert.Contains(palette.FilteredActions, r => r.InstallState == PackInstallState.NotInstalled);
        Assert.True(new SettingsService(path).Settings.ShowNotInstalledActions);
    }

    [Fact]
    public void ReadOnlyPreferencesCannotBeChangedAndGermanNumbersUseCurrentCulture()
    {
        using var temp = new TempDirectory();
        string path = temp.Write("future.json", "{\"Version\":99}");
        using var vm = new ActionPacksViewModel(new FakeManagement(), new SettingsService(path));
        Assert.False(vm.CanChangeVisibility); vm.ShowNotInstalledActions = false; Assert.True(vm.ShowNotInstalledActions);
        var previous = CultureInfo.CurrentCulture; var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");
            var row = new ActionPackRow(FakeManagement.Items[0]);
            Assert.Equal("2,0 MB", row.SizeText);
            Assert.Contains("Aktionen", row.Details);
            Assert.Equal("Installiert", row.StateText);
        }
        finally { CultureInfo.CurrentCulture = previous; CultureInfo.CurrentUICulture = previousUi; }
    }

    private sealed class TestCatalog(IReadOnlyList<CatalogEntry> entries) : IActionCatalog { public IReadOnlyList<CatalogEntry> Entries => entries; }
    private sealed class GeneratedPack : IActionPack
    {
        public ActionPackInfo Info => new("powertrove.fixture", "Generated bundled tools", "0.1.0", "PowerTrove tests");
        public IReadOnlyList<IAction> Actions => [new InstalledAction()];
    }
    private sealed class NoWorkerFactory : IPackWorkerProcessFactory
    {
        public int Starts;
        public Task<IPackWorkerProcess> StartAsync(PackWorkerGroup group, string workDirectory, CancellationToken token)
        { Starts++; throw new InvalidOperationException("Pack listing must not launch a worker."); }
    }
    private sealed class InstalledAction : IAction
    {
        public ActionDescriptor Descriptor { get; } = new("powertrove.fixture.installed", "Generated fixture action", "Generated fixture", new HashSet<ContentKind> { ContentKind.Files }, [], true, true);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) => new(new ActionPreview(true, null, [], []));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(new ActionReceipt(true, false, [], [], false));
    }
    private sealed class FakeManagement : IPackManagementService
    {
        public static readonly PackListItem[] Items =
        [new("powertrove.installed", "Generated installed", "PowerTrove tests", "0.1.0", "0.1.0", 2097152, 2, PackInstallState.Installed, false, true, false),
         new("powertrove.missing", "Generated missing", "PowerTrove tests", null, "0.1.0", 1000, 1, PackInstallState.NotInstalled, true, false, false),
         new("powertrove.update", "Generated update", "PowerTrove tests", "0.1.0", "0.2.0", 1000, 1, PackInstallState.UpdateAvailable, true, true, false),
         new("powertrove.incompatible", "Generated incompatible", "PowerTrove tests", null, "99.0.0", 1000, 1, PackInstallState.Incompatible, false, false, false),
         new("powertrove.recipes", "Generated host recipes", "PowerTrove tests", "0.1.0", "0.1.0", 0, 2, PackInstallState.Installed, false, false, true)];
        public int Opens, LocalLoads, Canceled;
        public bool HoldOpen;
        public PackChannelStatus Status = PackChannelStatus.Ready;
        public List<string> Commands { get; } = [];
        public async Task<PackPageSnapshot> OpenOnUserRequestAsync(CancellationToken token)
        {
            Opens++;
            if (HoldOpen)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { Canceled++; throw; }
            }
            return new(Status, Items);
        }
        public Task<PackPageSnapshot> GetLocalAsync(CancellationToken token) { LocalLoads++; return Task.FromResult(new PackPageSnapshot(Status, Items)); }
        public Task<PackOfferResult> PrepareAsync(string id, CancellationToken token)
        { var row = Items.Single(r => r.Id == id); return Task.FromResult(new PackOfferResult(PackChannelStatus.Ready,
            new PackInstallOffer(id, row.AvailableVersion, row.Name, row.Publisher, row.SizeBytes, "generated"))); }
        public Task<PackChannelStatus> InstallAsync(PackInstallOffer offer, IProgress<PackDownloadProgress>? progress, CancellationToken token)
        { Commands.Add("install:" + offer.Id); return Task.FromResult(PackChannelStatus.Installed); }
        public Task<PackChannelStatus> InstallFileAsync(string path, CancellationToken token) { Commands.Add("file"); return Task.FromResult(PackChannelStatus.Installed); }
        public Task<PackChannelStatus> UninstallAsync(string id, CancellationToken token) { Commands.Add("remove:" + id); return Task.FromResult(PackChannelStatus.Ready); }
    }
}
