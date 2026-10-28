using System.Globalization;
using System.Text.Json;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Core.Catalog;
using PowerTrove.Core.Search;

namespace PowerTrove.Core.Tests.Catalog;

public sealed class ActionCatalogTests
{
    internal static CatalogEntry Entry(string id = "powertrove.pdf.catalog", IReadOnlyList<string>? extensions = null) => new(
        new ManifestAction(id, "PDF Tool", new[] { "files" }, "Process PDF files.",
            Selection: ManifestSelection.FromRequirements(SelectionRequirements.Any("pdf")),
            Extensions: extensions ?? new[] { ".pdf" }, ChangesFiles: true),
        new ActionPackInfo("powertrove.pack.catalog", "PDF Tools", "1.0.0", "PowerTrove"),
        PackInstallState.NotInstalled, 4 * 1024 * 1024);

    internal static string Json(params CatalogEntry[] entries) => JsonSerializer.Serialize(new
    {
        schemaVersion = "1",
        packs = entries.GroupBy(e => e.Pack.Id).Select(g => new
        {
            manifest = new ActionManifest("2.0", g.Key, g.First().Pack.Version, g.First().Pack.DisplayName,
                g.First().Pack.Publisher, "1", g.Select(e => e.Action).ToArray(), MinHostVersion: "0.1.0", Capabilities: Array.Empty<string>()),
            downloadBytes = g.First().DownloadBytes
        }).ToArray()
    });

    internal static ContextSnapshot Snapshot(params string[] extensions) => new(Guid.NewGuid(),
        new SourceWindowIdentity(0, 0, null, DateTimeOffset.UtcNow), ContextStatus.Available,
        new FileSelection(extensions.Select(e => new SelectedFile(null, "synthetic" + e, e, null, 0, false, false, null)).ToArray()),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "synthetic", null);

    [Theory]
    [InlineData("")]
    [InlineData("PDF")]
    public async Task MissingPdfPack_IsDiscoverableWithReasonAndLosesExactly150Points(string query)
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            var missing = new CatalogAction(Entry());
            var registry = new ActionRegistry();
            registry.Register(new InstalledAction(missing.Descriptor with { Id = "powertrove.pdf.installed" }));
            registry.RegisterCatalog(ActionCatalog.Read(Json(Entry()), Array.Empty<ActionPackInfo>(), new Version(0, 1, 0)));
            var results = await new ActionSearchEngine(registry).SearchAsync(query, Snapshot(".PDF"));
            Assert.Equal(2, results.Count);
            Assert.Equal(PackInstallState.Installed, results[0].InstallState);
            Assert.Equal(PackInstallState.NotInstalled, results[1].InstallState);
            Assert.True(results[1].IsCompatible);
            Assert.Equal("Requires PDF Tools (download about 4 MB).", results[1].AvailabilityReason);
            Assert.Equal(results[0].Rank - 150, results[1].Rank);
            Assert.Equal(SelectionFit.Ideal, results[1].Fit);
            Assert.NotEqual(ActionHighlight.BestFit, results[1].Highlight);
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    [Fact]
    public async Task PreviewAndExecution_AreAlwaysBlockedWithoutOutputs()
    {
        var action = new CatalogAction(Entry());
        var preview = await action.PreviewAsync(Snapshot(".pdf"), new EmptyParameters(), default);
        Assert.True(preview.IsBlocked);
        Assert.Equal(action.DownloadReason, preview.PreviewText);
        Assert.Empty(preview.ExpectedOutputs);
        var receipt = await action.ExecuteAsync(Snapshot(".pdf"), new EmptyParameters(), null, default);
        Assert.False(receipt.IsSuccess);
        Assert.False(receipt.IsUndoable);
        Assert.Empty(receipt.OutputPaths);
        Assert.Equal(action.DownloadReason, Assert.Single(receipt.Failures).ErrorMessage);
        Assert.False(action.Descriptor.CanRememberParameters);
        Assert.True(action.Descriptor.RequiresPreview);
        Assert.Null(action.Descriptor.Parameters);
    }

    [Fact]
    public async Task StaticMatching_ChecksKindExtensionFamilyRolesAndCounts_WithoutReadingFiles()
    {
        var action = new CatalogAction(Entry(extensions: new[] { ".jpg" }));
        Assert.False((await action.MatchAsync(Snapshot(".pdf"), default)).IsAvailable);
        var entry = Entry() with { Action = Entry().Action with
        {
            Selection = ManifestSelection.FromRequirements(SelectionRequirements.Multiple("pdf"))
        }};
        action = new CatalogAction(entry);
        Assert.False((await action.MatchAsync(Snapshot(".pdf"), default)).IsAvailable);
        Assert.True((await action.MatchAsync(Snapshot(".pdf", ".pdf"), default)).IsAvailable);
        Assert.False((await action.MatchAsync(Snapshot() with { Content = new TextSelection("synthetic") }, default)).IsAvailable);
        Assert.False((await action.MatchAsync(Snapshot(".pdf", ".pdf") with { Status = ContextStatus.Denied }, default)).IsAvailable);
        var roles = SelectionRequirements.Any("pdf", "image") with
        {
            FamilyLimits = new Dictionary<string, FamilyLimit> { ["pdf"] = new(1, 1), ["image"] = new(1, 1) }
        };
        action = new CatalogAction(Entry() with { Action = Entry().Action with
        {
            Extensions = new[] { ".pdf", ".png" }, Selection = ManifestSelection.FromRequirements(roles)
        }});
        Assert.False((await action.MatchAsync(Snapshot(".pdf"), default)).IsAvailable);
        Assert.True((await action.MatchAsync(Snapshot(".pdf", ".png"), default)).IsAvailable);
        Assert.False((await action.MatchAsync(Snapshot(".pdf", ".png", ".png"), default)).IsAvailable);
    }

    [Fact]
    public async Task CapabilitiesAndCancellation_AreHonored()
    {
        var action = new CatalogAction(Entry() with { Action = Entry().Action with { RequiredCapabilities = new[] { "files.v1" } } });
        Assert.False((await action.MatchAsync(Snapshot(".pdf"), default)).IsAvailable);
        Assert.True((await action.MatchAsync(Snapshot(".pdf") with { ProvidedCapabilities = new HashSet<string> { "files.v1" } }, default)).IsAvailable);
        var ct = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await action.MatchAsync(Snapshot(".pdf"), ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await action.PreviewAsync(Snapshot(".pdf"), new EmptyParameters(), ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action.ExecuteAsync(Snapshot(".pdf"), new EmptyParameters(), null, ct));
    }

    [Fact]
    public void Registry_RejectsCatalogCollisionsAtomically_InEitherRegistrationOrder()
    {
        var entry = Entry();
        var real = new InstalledAction(new CatalogAction(entry).Descriptor);
        var registry = new ActionRegistry(); registry.Register(real);
        var catalog = ActionCatalog.Read(Json(Entry("powertrove.pdf.unique"), entry), Array.Empty<ActionPackInfo>(), new Version(0, 1, 0));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterCatalog(catalog));
        Assert.Same(real, Assert.Single(registry.GetAllActions()));
        var reverse = new ActionRegistry(); reverse.Register(new CatalogAction(entry));
        Assert.Throws<InvalidOperationException>(() => reverse.Register(real));
        Assert.Throws<InvalidOperationException>(() => reverse.Register(new InstalledAction(real.Descriptor with { Id = real.Descriptor.Id.ToUpperInvariant() })));
    }

    [Fact]
    public void InstalledOrIncompatiblePacks_NeverRegisterCatalogActions()
    {
        var entry = Entry();
        foreach (var version in new[] { "0.5.0", "1.0.0" })
        {
            var catalog = ActionCatalog.Read(Json(entry), new[] { entry.Pack with { Version = version } }, new Version(0, 1, 0));
            Assert.Equal(version == "1.0.0" ? PackInstallState.Installed : PackInstallState.UpdateAvailable, catalog.Entries[0].State);
            var registry = new ActionRegistry(); registry.RegisterCatalog(catalog);
            Assert.Empty(registry.GetAllActions());
        }
        var incompatible = ActionCatalog.Read(Json(entry).Replace("0.1.0", "99.0.0"), Array.Empty<ActionPackInfo>(), new Version(0, 1, 0));
        Assert.Equal(PackInstallState.Incompatible, incompatible.Entries[0].State);
        var empty = new ActionRegistry(); empty.RegisterCatalog(incompatible); Assert.Empty(empty.GetAllActions());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":\"1\",\"packs\":null}")]
    [InlineData("{\"schemaVersion\":\"1\",\"schemaVersion\":\"1\",\"packs\":[]}")]
    [InlineData("{\"schemaVersion\":\"3\",\"packs\":[]}")]
    public void MalformedCatalogs_UseContentFreeTypedErrors(string json)
    {
        var ex = Assert.Throws<CatalogFormatException>(() => ActionCatalog.Read(json, Array.Empty<ActionPackInfo>(), new Version(0, 1, 0)));
        Assert.Equal("Invalid action catalog.", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void BoundsAndDuplicateIds_AreRejected()
    {
        void Reject(string json) => Assert.Throws<CatalogFormatException>(() => ActionCatalog.Read(json, Array.Empty<ActionPackInfo>(), new Version(0, 1, 0)));
        Reject(new string(' ', ActionCatalog.MaximumJsonBytes + 1));
        Reject(Json(Entry(), Entry()));
        Reject(Json(Entry(), Entry() with { Pack = Entry().Pack with { Id = "powertrove.pack.other" } }));
        Reject(Json(Entry() with { DownloadBytes = -1 }));
        var tooMany = Enumerable.Range(0, ActionCatalog.MaximumPacks + 1).Select(i => Entry("action" + i) with { Pack = Entry().Pack with { Id = "pack" + i } }).ToArray();
        Reject(Json(tooMany));
        var valid = Json(Entry());
        for (int i = 0; i < valid.Length; i += 17) Reject(valid[..i]);
    }

    [Fact]
    public void DownloadReasonAndAccessibleText_AreLocalized_AndUnknownSizeIsHonest()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");
            var action = new CatalogAction(Entry());
            Assert.Contains("Benötigt PDF Tools", action.DownloadReason);
            Assert.Contains("Download erforderlich", PowerTrove.Core.Resources.Strings.CatalogAccessibleName("PDF", action.DownloadReason));
            action = new CatalogAction(Entry() with { DownloadBytes = 0 });
            Assert.Contains("noch nicht verfügbar", action.DownloadReason);
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    private sealed class InstalledAction(ActionDescriptor descriptor) : IAction
    {
        public ActionDescriptor Descriptor { get; } = descriptor;
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken ct) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
