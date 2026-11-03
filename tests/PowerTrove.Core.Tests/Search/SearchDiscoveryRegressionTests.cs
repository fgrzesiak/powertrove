using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Core.Catalog;
using PowerTrove.Core.Search;

namespace PowerTrove.Core.Tests.Search;

public sealed class SearchDiscoveryRegressionTests
{
    [Fact]
    public async Task ExactVisibleTitleIsFoundEvenWhenCurrentContextCannotRunIt()
    {
        var unavailable = new Action("powertrove.pdf.merge", "PDFs zusammenführen", ContentKind.Files);
        var unrelated = new Action("powertrove.image.resize", "Bilder verkleinern", ContentKind.None);
        var results = await Search("PDFs zusammenführen", unavailable, unrelated);
        var hit = Assert.Single(results);
        Assert.Equal(unavailable.Descriptor.Id, hit.Action.Descriptor.Id);
        Assert.Equal(110, hit.TextMatchScore);
        Assert.False(hit.IsCompatible);
        Assert.Equal(0, unavailable.MatchCalls); // Failed static compatibility must not execute a pack matcher.
        Assert.Equal(0, unrelated.MatchCalls); // Unrelated actions must not run a matcher either.
    }

    [Theory]
    [InlineData("convert-to-webp")]
    [InlineData("convert to webp")]
    [InlineData("webp convert")]
    [InlineData("conevrt webp")]
    [InlineData("convert picture webp")]
    [InlineData("powertrove image webp")]
    public async Task HumanQueriesMatchTitleCombinedMetadataAndId(string query)
    {
        var action = new Action("powertrove.image.webp", "Convert to WebP", ContentKind.None,
            "picture", "image format");
        Assert.Contains(await Search(query, action), r => r.Action == action);
    }

    [Fact]
    public async Task ExactLongTitleIsNotExcludedByFuzzyInputBudget()
    {
        var title = "Command " + new string('x', 270);
        var action = new Action("long.title", title, ContentKind.None);
        var hit = Assert.Single(await Search(title, action));
        Assert.Same(action, hit.Action);
        Assert.Equal(110, hit.TextMatchScore);
    }

    [Fact]
    public async Task ExactTitleBeatsFuzzyResultsEvenWhenOtherActionIsFavored()
    {
        var exact = new Action("pdf.one", "Merge", ContentKind.None);
        var fuzzy = new Action("pdf.two", "Merge PDF pages", ContentKind.None);
        var registry = new ActionRegistry();
        registry.Register(exact); registry.Register(fuzzy);
        var engine = new ActionSearchEngine(registry, recentUsageScores: new Dictionary<string, int> { [fuzzy.Descriptor.Id] = 1000 });
        var hits = await engine.SearchAsync("Merge", Snapshot());
        Assert.Same(exact, hits[0].Action);
    }

    [Fact]
    public async Task PreviouslyPublishedPackWithoutSearchTermsRemainsSearchable()
    {
        var manifest = new ActionManifest("2.0", "powertrove.pdf", "1.0.0", "PDF Tools", "PowerTrove", "1",
            new[] { new ManifestAction("powertrove.pdf.merge", "Merge PDFs", new[] { "none" }, ChangesFiles: false) },
            MinHostVersion: "0.1.0", Capabilities: Array.Empty<string>());
        var catalog = LoadCatalog(manifest);
        var action = new CatalogAction(Assert.Single(catalog.Entries));
        var hit = Assert.Single(await Search("merge pdfs", action));
        Assert.Equal(action.Descriptor.Id, hit.Action.Descriptor.Id);
    }

    [Fact]
    public async Task NewSignedCatalogExposesEnglishAndLocalizedAliases()
    {
        var english = new ActionManifest("2.0", "powertrove.pdf", "1.0.0", "PDF Tools", "PowerTrove", "1",
            new[] { new ManifestAction("powertrove.pdf.merge", "Merge PDFs", new[] { "none" },
                ChangesFiles: false, SearchTerms: new[] { "combine pdf" }) },
            MinHostVersion: "0.1.0", Capabilities: Array.Empty<string>());
        var german = english with { DisplayName = "PDF-Werkzeuge", Actions = new[]
        {
            english.Actions[0] with { Title = "PDFs zusammenführen", SearchTerms = new[] { "pdf verbinden" } }
        } };
        var manifest = ManifestLocalization.WithTranslation(english, "de", german);
        var old = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("de");
            var catalog = LoadCatalog(manifest);
            var action = new CatalogAction(Assert.Single(catalog.Entries));
            Assert.Contains("combine pdf", action.Descriptor.SearchTerms);
            Assert.Contains("pdf verbinden", action.Descriptor.SearchTerms);
            Assert.Contains(await Search("combine pdf", action), r => r.Action == action);
            Assert.Contains(await Search("pdf verbinden", action), r => r.Action == action);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = old; }
    }

    private static IActionCatalog LoadCatalog(ActionManifest manifest)
    {
        string json = "{\"schemaVersion\":\"1\",\"packs\":[{\"manifest\":"
            + ActionPackManifest.ToJson(manifest) + ",\"downloadBytes\":1024}]}";
        return ActionCatalog.Read(json, Array.Empty<ActionPackInfo>(), new Version(9, 0), os: "windows", architecture: "x64");
    }

    private static async Task<IReadOnlyList<ActionSearchResult>> Search(string query, params IAction[] actions)
    {
        var registry = new ActionRegistry();
        foreach (var action in actions) registry.Register(action);
        return await new ActionSearchEngine(registry).SearchAsync(query, Snapshot());
    }

    private static ContextSnapshot Snapshot() => new(Guid.NewGuid(),
        new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available,
        new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "test", null);

    private sealed class Action(string id, string title, ContentKind accepts, params string[] searchTerms) : IAction
    {
        public ActionDescriptor Descriptor { get; } = new(id, title, "", new HashSet<ContentKind> { accepts },
            searchTerms, false, false);
        public int MatchCalls { get; private set; }
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken ct)
        {
            MatchCalls++;
            return ValueTask.FromResult(new ActionAvailability(true));
        }
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters,
            IProgress<ActionProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
