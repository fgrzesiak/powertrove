using System.Text.Json;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Core.Tests.Catalog;

public sealed class PackVersionSelectionTests
{
    [Fact]
    public void NewestCompatibleVersionIsSelected_AndNewerIncompatibleVersionDoesNotReplaceIt()
    {
        string json = Catalog(Manifest("1.0.0", "1.0.0"), Manifest("1.1.0", "1.0.0"), Manifest("2.0.0", "2.0.0"));
        var compatible = ActionCatalog.Read(json, [], new Version(1, 0, 0), "windows", "x64");
        Assert.All(compatible.Entries, e => { Assert.Equal("1.1.0", e.Pack.Version); Assert.Equal(PackInstallState.NotInstalled, e.State); });
        var current = ActionCatalog.Read(json, [], new Version(2, 0, 0), "windows", "x64");
        Assert.All(current.Entries, e => Assert.Equal("2.0.0", e.Pack.Version));
    }

    [Fact]
    public void NewCatalogActionsStayDownloadOnly_AndExistingInstalledDescriptorsArePreserved()
    {
        var registry = new ActionRegistry(); var installed = new LocalAction(); registry.RegisterPack("powertrove.images", [installed]);
        var newer = Manifest("1.1.0", "1.0.0") with { Actions = [Action("powertrove.image.first", "Changed remote title"), Action("powertrove.image.new", "New image tool")] };
        registry.ReplaceCatalog(ActionCatalog.Read(Catalog(newer), [new("powertrove.images", "Images", "1.0.0", "PowerTrove")], new Version(1, 0, 0), "windows", "x64"));
        Assert.Same(installed, registry.GetAction(installed.Descriptor.Id));
        var missing = Assert.IsType<CatalogAction>(registry.GetAction("powertrove.image.new"));
        Assert.Equal(PackInstallState.UpdateAvailable, missing.InstallState); Assert.NotEmpty(missing.ManifestFingerprint!);
        registry.ReplaceCatalog(ActionCatalog.Empty); Assert.Same(installed, registry.GetAction(installed.Descriptor.Id));
        Assert.Null(registry.GetAction("powertrove.image.new"));
    }

    [Fact]
    public void UnsupportedHostAndUnknownCapabilitiesFailClosed_EvenWhenPackWasPreviouslyInstalled()
    {
        var manifest = Manifest("2.0.0", "2.0.0");
        var result = ActionCatalog.Read(Catalog(manifest), [new("powertrove.images", "Images", "1.0.0", "PowerTrove")], new Version(1, 0, 0), "windows", "x64");
        Assert.All(result.Entries, e => Assert.Equal(PackInstallState.Incompatible, e.State));
        Assert.False(PackCompatibility.IsSupported(manifest with { MinHostVersion = "1.0.0", Capabilities = ["unknown.service.v99"] }, new Version(1, 0, 0), "windows", "x64"));
    }

    [Fact]
    public void DuplicateVersionsAndCrossPackActionOwnershipAreRejected()
    {
        var manifest = Manifest("1.0.0", "1.0.0");
        Assert.Throws<CatalogFormatException>(() => ActionCatalog.Read(Catalog(manifest, manifest), [], new Version(1, 0, 0)));
        Assert.Throws<CatalogFormatException>(() => ActionCatalog.Read(Catalog(manifest, manifest with { Id = "powertrove.other" }), [], new Version(1, 0, 0)));
    }

    [Fact]
    public void WorkerCatalogSelectsSupportedFileRelease_WithoutRestrictingGenericDiscovery()
    {
        var files = Manifest("1.0.0", "1.0.0");
        var table = Manifest("2.0.0", "1.0.0") with
        {
            Actions = [new ManifestAction("powertrove.image.first", "Generated table tool", ["table"], ChangesFiles: true)]
        };
        string json = Catalog(files, table);
        var discovery = ActionCatalog.Read(json, [], new Version(1, 0, 0), "windows", "x64");
        Assert.Equal("2.0.0", Assert.Single(discovery.Entries).Pack.Version);
        var worker = ActionCatalog.Read(json, [], new Version(1, 0, 0), "windows", "x64", requireWorkerInput: true);
        Assert.Equal("1.0.0", Assert.Single(worker.Entries).Pack.Version);
        var unsupported = ActionCatalog.Read(Catalog(table), [], new Version(1, 0, 0), "windows", "x64", requireWorkerInput: true);
        Assert.Equal(PackInstallState.Incompatible, Assert.Single(unsupported.Entries).State);
    }

    private static ActionManifest Manifest(string version, string minimum) => new("2.0", "powertrove.images", version, "Images", "PowerTrove", "1",
        [Action("powertrove.image.first", "Image tool")],
        PlatformConstraints: new(["windows"], ["x64"]), MinHostVersion: minimum, Capabilities: []);
    private static ManifestAction Action(string id, string title) => new(id, title, ["files"], Extensions: [".png"],
        Selection: ManifestSelection.FromRequirements(SelectionRequirements.Any("image")), ChangesFiles: true);
    private static string Catalog(params ActionManifest[] manifests) => JsonSerializer.Serialize(new
    { schemaVersion = "2", packs = manifests.Select(m => new { manifest = m, downloadBytes = 1000 }).ToArray() });
    private sealed class LocalAction : IAction
    {
        public ActionDescriptor Descriptor { get; } = new("powertrove.image.first", "Installed title", "Generated", new HashSet<ContentKind> { ContentKind.Files }, [], false, false);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken token) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken token) => throw new NotSupportedException();
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken token) => throw new NotSupportedException();
    }
}
