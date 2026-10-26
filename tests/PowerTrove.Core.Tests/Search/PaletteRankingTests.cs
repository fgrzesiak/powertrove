using PowerTrove.Core.Search;

namespace PowerTrove.Core.Tests.Search;

public class PaletteRankingTests
{
    [Fact]
    public async Task ExactQueryBeatsHigherFitHistoryAndExplicitWeight()
    {
        var exact = new TestAction("contextbar.image.compress", "Compress", SelectionRequirements.Any("image") with { IdealMinItems = 2 });
        var weak = new TestAction("contextbar.image.resize", "Compress images", SelectionRequirements.Any("image"), weight: 500);
        var signals = new Signals(weak.Descriptor.Id);
        var results = await Search(ImageContext(1), "Compress", signals, exact, weak);
        Assert.Equal(exact.Descriptor.Id, results[0].Action.Descriptor.Id);
        Assert.Equal(SelectionFit.Acceptable, results[0].Fit);
        Assert.Equal(SelectionFit.Ideal, results[1].Fit);
        Assert.True(results[1].Rank > results[0].Rank); // text intent deliberately precedes aggregate context score
        Assert.True(results[0].TextMatchScore > results[1].TextMatchScore);
    }

    [Fact]
    public async Task IncompatibleExactMatchCannotOutrankUsableResultsEvenWithLargeWeight()
    {
        var unavailable = new TestAction("contextbar.pdf.compress", "Compress", SelectionRequirements.Any("pdf"), weight: 5000, available: false);
        var usable = new TestAction("contextbar.image.compress", "Compress images", SelectionRequirements.Any("image"));
        var results = await Search(ImageContext(1), "Compress", null, unavailable, usable);
        Assert.Same(usable, results[0].Action);
        Assert.False(results[1].IsCompatible);
        Assert.Equal(ActionHighlight.None, results[1].Highlight);
    }

    [Fact]
    public async Task CommonImageTaskBreaksTiesBeforeAlphabeticalRotationAndGenericTools()
    {
        var rotate = new TestAction("contextbar.image.auto-rotate", "Auto rotate", SelectionRequirements.Any("image"));
        var convert = new TestAction("contextbar.image.convert-webp", "Convert to WebP", SelectionRequirements.Any("image"));
        var generic = new TestAction("contextbar.files.zip", "Archive", SelectionRequirements.Any());
        var results = await Search(ImageContext(1), "", null, rotate, generic, convert);
        Assert.Equal(1350, results[0].Rank);
        Assert.Same(convert, results[0].Action);
        Assert.Equal(ActionHighlight.BestFit, results[0].Highlight);
        Assert.Same(rotate, results[1].Action);
        Assert.Same(generic, results[2].Action);
    }

    [Fact]
    public async Task MultipleImagesPreferACompatibleCombinationWhileUsageStillWins()
    {
        var collage = new TestAction("contextbar.image.collage", "Collage", SelectionRequirements.Multiple("image"));
        var convert = new TestAction("contextbar.image.convert-webp", "Convert", SelectionRequirements.Any("image"));
        var results = await Search(ImageContext(2), "", null, convert, collage);
        Assert.Same(collage, results[0].Action);
        results = await Search(ImageContext(2), "", new Signals(convert.Descriptor.Id), convert, collage);
        Assert.Same(convert, results[0].Action);
        Assert.Equal(ActionHighlight.LastUsedForSelection, results[0].Highlight);
    }

    [Fact]
    public async Task StaleUnavailableHistoryDoesNotSuppressRecommendation()
    {
        var action = new TestAction("contextbar.image.resize", "Resize", SelectionRequirements.Any("image"));
        var results = await Search(ImageContext(1), "", new Signals("removed.action"), action);
        Assert.Equal(ActionHighlight.BestFit, Assert.Single(results).Highlight);
    }

    private static async Task<IReadOnlyList<ActionSearchResult>> Search(ContextSnapshot context, string query, Signals? signals, params IAction[] actions)
    {
        var registry = new ActionRegistry();
        foreach (var action in actions) registry.Register(action);
        var engine = signals is null ? new ActionSearchEngine(registry) : new ActionSearchEngine(registry, signals);
        return await engine.SearchAsync(query, context);
    }
    private static ContextSnapshot ImageContext(int count) => new(Guid.NewGuid(), new SourceWindowIdentity(0, 1, "test", DateTimeOffset.UtcNow), ContextStatus.Available,
        new FileSelection(Enumerable.Range(0, count).Select(i => new SelectedFile($"image{i}.png", $"image{i}.png", ".png", "image/png", 100, true, true, null)).ToArray()),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "test", null);
    private sealed class Signals(string lastUsed) : IActionUsageSignals
    {
        public string? LastUsedAction(string signatureKey) => lastUsed;
        public double SelectionScore(string signatureKey, string actionId) => lastUsed == actionId ? 100 : 0;
        public void Refresh() { }
    }
    private sealed class TestAction(string id, string title, SelectionRequirements selection, int weight = 0, bool available = true) : IAction
    {
        public ActionDescriptor Descriptor { get; } = new(id, title, "", new HashSet<ContentKind> { ContentKind.Files }, Array.Empty<string>(), false, false, ExplicitWeight: weight, Selection: selection);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken ct) => new(new ActionAvailability(available));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
