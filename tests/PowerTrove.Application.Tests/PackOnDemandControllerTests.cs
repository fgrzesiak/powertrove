using PowerTrove.Abstractions.Manifest;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Application.Packs;
using PowerTrove.Application.ViewModels;
using PowerTrove.Core;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Application.Tests;

public sealed class PackOnDemandControllerTests
{
    [Fact]
    public async Task DeclineCallsNoServiceAndPreservesTheExactSelection()
    {
        using var fixture = new Fixture();
        using var controller = new PackOnDemandController(fixture.Service, fixture.Registry);
        var before = fixture.Snapshot;
        var result = await controller.ResolveAsync(fixture.Missing, before, (_, _) => Task.FromResult(false), () => true);
        Assert.Equal(PackOnDemandOutcome.Declined, result.Outcome);
        Assert.Equal(0, fixture.Service.Prepares); Assert.Equal(0, fixture.Service.Installs);
        Assert.Same(fixture.Missing, fixture.Registry.GetAction(fixture.Missing.Descriptor.Id));
        Assert.Same(before, fixture.Snapshot); Assert.Equal("original", File.ReadAllText(fixture.Input));
    }

    [Fact]
    public async Task SuccessReresolvesTheRealActionWithTheSameSnapshotAndNeverExecutesIt()
    {
        using var fixture = new Fixture();
        using var controller = new PackOnDemandController(fixture.Service, fixture.Registry);
        int consents = 0;
        var result = await controller.ResolveAsync(fixture.Missing, fixture.Snapshot, (details, _) =>
        { consents++; Assert.Equal("PowerTrove tests", details.Publisher); Assert.Equal(1000, details.DownloadBytes); return Task.FromResult(true); }, () => true);
        Assert.Equal(PackOnDemandOutcome.Ready, result.Outcome);
        Assert.Same(fixture.Real, result.Action);
        Assert.Same(fixture.Snapshot, fixture.Real.MatchedSnapshot);
        Assert.Equal(0, fixture.Real.Executions); Assert.Equal(0, fixture.Real.Previews);
        Assert.Equal(1, consents); Assert.Equal(1, fixture.Service.Installs);
        Assert.Same(fixture.Service.Offer, fixture.Service.Approved);
    }

    [Fact]
    public async Task ChangedOfferRequiresFreshConsentBeforeAnyDownload()
    {
        using var fixture = new Fixture();
        fixture.Service.Offer = fixture.Service.Offer with { Version = "0.2.0", DownloadBytes = 2000 };
        using var controller = new PackOnDemandController(fixture.Service, fixture.Registry);
        var offers = new List<PackConsentDetails>();
        var result = await controller.ResolveAsync(fixture.Missing, fixture.Snapshot, (details, _) =>
        { offers.Add(details); return Task.FromResult(offers.Count == 1); }, () => true);
        Assert.Equal(PackOnDemandOutcome.Declined, result.Outcome);
        Assert.Equal(new[] { "0.1.0", "0.2.0" }, offers.Select(o => o.Version));
        Assert.Equal(2000, offers[1].DownloadBytes);
        Assert.Equal(0, fixture.Service.Installs);
    }

    [Fact]
    public async Task ExpiryDuringInstallLeavesThePackInstalledWithoutOpeningAnAction()
    {
        using var fixture = new Fixture();
        var clock = new PackHostSupervisorTests.TestClock();
        fixture.Service.AfterInstall = () => clock.Advance(TimeSpan.FromMinutes(10));
        using var controller = new PackOnDemandController(fixture.Service, fixture.Registry, clock);
        var result = await controller.ResolveAsync(fixture.Missing, fixture.Snapshot, (_, _) => Task.FromResult(true), () => true);
        Assert.Equal(PackOnDemandOutcome.Expired, result.Outcome);
        Assert.Equal("Select the files again.", result.Message);
        Assert.Null(result.Action); Assert.Null(fixture.Real.MatchedSnapshot);
        Assert.Equal(1, fixture.Service.Installs); Assert.Same(fixture.Real, fixture.Registry.GetAction(fixture.Missing.Descriptor.Id));
        Assert.Equal(0, fixture.Real.Executions);
    }

    [Fact]
    public async Task NewInvocationAndPrivateInstallationErrorsCannotOpenOptions()
    {
        using var fixture = new Fixture();
        bool current = true; fixture.Service.AfterInstall = () => current = false;
        using var controller = new PackOnDemandController(fixture.Service, fixture.Registry);
        var changed = await controller.ResolveAsync(fixture.Missing, fixture.Snapshot, (_, _) => Task.FromResult(true), () => current);
        Assert.Equal(PackOnDemandOutcome.SelectionChanged, changed.Outcome); Assert.Null(changed.Action);
        fixture.Registry.RegisterPack("powertrove.fixture", [fixture.Missing]);
        current = true; fixture.Service.ThrowPrivate = true;
        var failed = await controller.ResolveAsync(fixture.Missing, fixture.Snapshot, (_, _) => Task.FromResult(true), () => current);
        Assert.Equal(PackOnDemandOutcome.Failed, failed.Outcome); Assert.Null(failed.Action);
        Assert.DoesNotContain("private", failed.Message!); Assert.DoesNotContain(fixture.Input, failed.Message!);
    }

    [Fact]
    public async Task AnAlreadyInstalledActionSkipsAllConsentAndDownloadWork()
    {
        using var fixture = new Fixture();
        fixture.Registry.RegisterPack("powertrove.fixture", [fixture.Real]);
        using var controller = new PackOnDemandController(fixture.Service, fixture.Registry);
        var result = await controller.ResolveAsync(fixture.Missing, fixture.Snapshot, (_, _) => throw new InvalidOperationException(), () => true);
        Assert.Equal(PackOnDemandOutcome.Ready, result.Outcome); Assert.Same(fixture.Real, result.Action);
        Assert.Equal(0, fixture.Service.Prepares); Assert.Equal(0, fixture.Service.Installs);
    }

    [Fact]
    public async Task PreviewCannotUseAReplacementSnapshotOrActionBehindTheOptionsDialog()
    {
        using var fixture = new Fixture();
        fixture.Registry.RegisterPack("powertrove.fixture", [fixture.Real]);
        var vm = new PaletteViewModel();
        var engine = new PowerTrove.Core.Search.ActionSearchEngine(fixture.Registry);
        vm.Initialize(fixture.Snapshot, fixture.Registry, engine); await vm.SearchTask;
        var newer = fixture.Snapshot with { InvocationId = Guid.NewGuid() };
        vm.Initialize(newer, fixture.Registry, engine); await vm.SearchTask;
        var stalePreview = await vm.PreviewSelectedActionAsync(new EmptyParameters(), expectedSnapshot: fixture.Snapshot);
        Assert.True(stalePreview!.IsBlocked); Assert.Equal(0, fixture.Real.Previews);
        fixture.Registry.RegisterPack("powertrove.fixture", [new RealAction()]);
        var replacedAction = await vm.PreviewSelectedActionAsync(new EmptyParameters(), expectedSnapshot: newer, expectedAction: fixture.Real);
        Assert.True(replacedAction!.IsBlocked); Assert.Equal(0, fixture.Real.Previews);
    }

    [Fact]
    public async Task CancellationAndDuplicateEnterDoNotExecuteThePendingAction()
    {
        using var fixture = new Fixture(); fixture.Service.Block = true;
        using var controller = new PackOnDemandController(fixture.Service, fixture.Registry);
        var pending = controller.ResolveAsync(fixture.Missing, fixture.Snapshot, (_, _) => Task.FromResult(true), () => true);
        Assert.True(controller.IsPending);
        var duplicate = await controller.ResolveAsync(fixture.Missing, fixture.Snapshot, (_, _) => throw new InvalidOperationException(), () => true);
        Assert.Equal(PackOnDemandOutcome.Busy, duplicate.Outcome);
        controller.Dispose();
        Assert.Equal(PackOnDemandOutcome.Canceled, (await pending).Outcome);
        Assert.False(controller.IsPending); Assert.Equal(0, fixture.Real.Executions);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        public string Input { get; }
        public ContextSnapshot Snapshot { get; }
        public ActionRegistry Registry { get; } = new();
        public CatalogAction Missing { get; }
        public RealAction Real { get; } = new();
        public FakeService Service { get; }
        public Fixture()
        {
            Input = _temp.Write("input.txt", "original"); Snapshot = Snapshots.ForPaths(Input);
            Missing = new CatalogAction(new CatalogEntry(new ManifestAction("powertrove.fixture.copy", "Generated copy", ["files"],
                Selection: new ManifestSelection(["text"], 1, null, null, null, false), Extensions: [".txt"], ChangesFiles: true),
                new ActionPackInfo("powertrove.fixture", "Generated tools", "0.1.0", "PowerTrove tests"), PackInstallState.NotInstalled, 1000));
            Registry.Register(Missing); Service = new FakeService(Registry, Real);
        }
        public void Dispose() => _temp.Dispose();
    }
    private sealed class FakeService(ActionRegistry registry, RealAction real) : IPackManagementService
    {
        public PackInstallOffer Offer = new("powertrove.fixture", "0.1.0", "Generated tools", "PowerTrove tests", 1000, "generated");
        public PackInstallOffer? Approved;
        public int Prepares, Installs;
        public bool Block, ThrowPrivate;
        public Action? AfterInstall;
        public Task<PackOfferResult> PrepareAsync(string id, CancellationToken token)
        { Prepares++; if (ThrowPrivate) throw new IOException("Synthetic private path C:\\private\\pack.cbpack"); return Task.FromResult(new PackOfferResult(PackChannelStatus.Ready, Offer)); }
        public async Task<PackChannelStatus> InstallAsync(PackInstallOffer offer, IProgress<PackDownloadProgress>? progress, CancellationToken token)
        {
            Installs++; Approved = offer;
            if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            registry.RegisterPack(offer.Id, [real]); AfterInstall?.Invoke(); return PackChannelStatus.Installed;
        }
        public Task<PackPageSnapshot> OpenOnUserRequestAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<PackPageSnapshot> GetLocalAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<PackChannelStatus> InstallFileAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public Task<PackChannelStatus> UninstallAsync(string id, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class RealAction : IAction
    {
        public int Executions, Previews;
        public ContextSnapshot? MatchedSnapshot;
        public ActionDescriptor Descriptor { get; } = new("powertrove.fixture.copy", "Generated real copy", "Generated real action", new HashSet<ContentKind> { ContentKind.Files }, [], true, true);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
        { MatchedSnapshot = context; return new(new ActionAvailability(true)); }
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
        { Previews++; return new(new ActionPreview(true, null, [], [])); }
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        { Executions++; return Task.FromResult(new ActionReceipt(true, false, [], [], false)); }
    }
}
