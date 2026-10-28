using PowerTrove.Abstractions.Testing;
using PowerTrove.Application.Configuration;
using PowerTrove.Application.Packs;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Application.Tests;

public sealed class BuiltInPackMigrationTests
{
    [Fact]
    public void FreshInstallationsTakeOverNothing_ExistingOnesTakeOverEveryFormerlyCompiledInPackOnce()
    {
        var fresh = new FakeSettings(existing: false);
        Assert.False(new BuiltInPackMigration(new FakePacks(), fresh).Prepare(new HashSet<string>()));
        Assert.True(fresh.PackMigrationChecked); Assert.Empty(fresh.PendingPackMigration);

        var updated = new FakeSettings(existing: true);
        Assert.True(new BuiltInPackMigration(new FakePacks(), updated).Prepare(new HashSet<string> { "powertrove.files", "powertrove.pdf" }));
        Assert.Equal(BuiltInPackMigration.FormerlyBuiltIn.Except(["powertrove.files", "powertrove.pdf"]), updated.PendingPackMigration);
        // Checked once: a later removal in Settings is never undone by another start.
        updated.SetPackMigration([]);
        Assert.False(new BuiltInPackMigration(new FakePacks(), updated).Prepare(new HashSet<string>()));
    }

    [Fact]
    public async Task InstallsMissingPacks_SkipsInstalledOnes_AndKeepsFailuresForTheNextStart()
    {
        var settings = new FakeSettings(existing: true);
        settings.SetPackMigration(["powertrove.files", "powertrove.images", "powertrove.media", "powertrove.tables"]);
        var packs = new FakePacks
        {
            Rows = [Row("powertrove.files", PackInstallState.Installed, false), Row("powertrove.images", PackInstallState.NotInstalled, true),
                Row("powertrove.media", PackInstallState.NotInstalled, true), Row("powertrove.tables", PackInstallState.Incompatible, false)],
            Failing = { "powertrove.media" }
        };
        await new BuiltInPackMigration(packs, settings).RunAsync(default);
        Assert.Equal(["powertrove.images", "powertrove.media"], packs.Installed);
        Assert.Equal(["powertrove.media", "powertrove.tables"], settings.PendingPackMigration);
    }

    [Fact]
    public async Task OfflineChannelChangesNothing()
    {
        var settings = new FakeSettings(existing: true);
        settings.SetPackMigration(["powertrove.images"]);
        var packs = new FakePacks { Status = PackChannelStatus.Offline, Rows = [Row("powertrove.images", PackInstallState.NotInstalled, true)] };
        await new BuiltInPackMigration(packs, settings).RunAsync(default);
        Assert.Empty(packs.Installed);
        Assert.Equal(["powertrove.images"], settings.PendingPackMigration);
    }

    [Fact]
    public void SettingsPersistThePendingMigrationAndRecognizeAnExistingInstallation()
    {
        using var temp = new TempDirectory();
        string path = temp.File("settings.json");
        var first = new SettingsService(path);
        Assert.False(first.HadExistingSettings);
        first.SetPackMigration(["powertrove.images", "powertrove.images", "powertrove.pdf"]);
        var second = new SettingsService(path);
        Assert.True(second.HadExistingSettings);
        Assert.True(second.PackMigrationChecked);
        Assert.Equal(["powertrove.images", "powertrove.pdf"], second.PendingPackMigration);
    }

    [Fact]
    public void FreshInstallationsInstallTheirSetupSelection_AndTheSelectionFileIsConsumedOnce()
    {
        using var temp = new TempDirectory();
        string file = temp.Write("setup-packs.json", """{"packs":["powertrove.pdf","powertrove.images","powertrove.pdf","example.unofficial",42]}""");
        var selection = BuiltInPackMigration.TakeSetupSelection(file);
        Assert.Equal(["powertrove.pdf", "powertrove.images"], selection);
        Assert.False(File.Exists(file));
        Assert.Empty(BuiltInPackMigration.TakeSetupSelection(file));

        var settings = new FakeSettings(existing: false);
        Assert.True(new BuiltInPackMigration(new FakePacks(), settings).Prepare(new HashSet<string>(), selection));
        Assert.Equal(["powertrove.pdf", "powertrove.images"], settings.PendingPackMigration);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""["powertrove.pdf"]""")]
    [InlineData("""{"packs":"powertrove.pdf"}""")]
    public void MalformedSetupSelectionsSelectNothingAndAreRemoved(string content)
    {
        using var temp = new TempDirectory();
        string file = temp.Write("setup-packs.json", content);
        Assert.Empty(BuiltInPackMigration.TakeSetupSelection(file));
        Assert.False(File.Exists(file));
    }

    private static PackListItem Row(string id, PackInstallState state, bool canDownload) =>
        new(id, id, "PowerTrove", state == PackInstallState.Installed ? "0.1.0" : null, "0.1.0", 1000, 1, state, canDownload, false, false);

    private sealed class FakeSettings(bool existing) : IPackMigrationSettings
    {
        public bool HadExistingSettings => existing;
        public bool PackMigrationChecked { get; private set; }
        public IReadOnlyList<string> PendingPackMigration { get; private set; } = [];
        public void SetPackMigration(IReadOnlyList<string> pending) { PackMigrationChecked = true; PendingPackMigration = pending.ToArray(); }
    }

    private sealed class FakePacks : IPackManagementService
    {
        public PackChannelStatus Status { get; init; } = PackChannelStatus.Ready;
        public IReadOnlyList<PackListItem> Rows { get; init; } = [];
        public HashSet<string> Failing { get; } = new();
        public List<string> Installed { get; } = new();
        public Task<PackPageSnapshot> OpenOnUserRequestAsync(CancellationToken token) => Task.FromResult(new PackPageSnapshot(Status, Rows));
        public Task<PackPageSnapshot> GetLocalAsync(CancellationToken token) => Task.FromResult(new PackPageSnapshot(PackChannelStatus.Ready, Rows));
        public Task<PackOfferResult> PrepareAsync(string id, CancellationToken token) =>
            Task.FromResult(new PackOfferResult(PackChannelStatus.Ready, new PackInstallOffer(id, "0.1.0", id, "PowerTrove", 1000, "fingerprint")));
        public Task<PackChannelStatus> InstallAsync(PackInstallOffer offer, IProgress<PackDownloadProgress>? progress, CancellationToken token)
        {
            Installed.Add(offer.Id);
            return Task.FromResult(Failing.Contains(offer.Id) ? PackChannelStatus.InvalidData : PackChannelStatus.Installed);
        }
        public Task<PackChannelStatus> InstallFileAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public Task<PackChannelStatus> UninstallAsync(string id, CancellationToken token) => throw new NotSupportedException();
    }
}
