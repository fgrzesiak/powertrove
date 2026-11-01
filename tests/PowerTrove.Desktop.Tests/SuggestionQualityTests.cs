using PowerTrove.Core.Search;

namespace PowerTrove.Desktop.Tests;

/// <summary>
/// The suggestion test set: typical selections with the action a new user (no history) should see first,
/// actions that must be close behind, and actions that must not be offered. It runs the full built-in
/// catalog through the production search engine. Change an expectation only on purpose, with the reason.
/// </summary>
public sealed class SuggestionQualityTests : IDisposable
{
    private readonly SelectionFixtures _fixtures = new();

    public sealed record Case(string[] Files, string BestFit, string[] NearTop, string[] NotOffered)
    {
        public override string ToString() => string.Join(" + ", Files);
    }

    // NearTop: within the first six compatible results of the empty-query view.
    public static TheoryData<Case> Cases => new()
    {
        new(["a.jpg"], "powertrove.image.convert-webp", ["powertrove.image.compress", "powertrove.image.resize"], ["powertrove.pdf.merge", "powertrove.video.compress"]),
        // Several images: a PDF first, then the batch tasks and a collage (selection-intelligence S7).
        // Exactly two images lead with comparing them (smart-combinations C13).
        new(["a.jpg", "b.jpg", "c.jpg"], "powertrove.pdf.images-to-pdf", ["powertrove.image.resize", "powertrove.image.compress", "powertrove.image.collage"], ["powertrove.pdf.merge", "powertrove.audio.convert-mp3", "powertrove.image.compare"]),
        new(["a.jpg", "b.jpg", "c.jpg", "d.png", "e.png", "f.png"], "powertrove.pdf.images-to-pdf", ["powertrove.image.resize", "powertrove.image.compress", "powertrove.image.collage"], ["powertrove.image.compare"]),
        new(["a.png", "b.png"], "powertrove.image.compare", ["powertrove.pdf.images-to-pdf", "powertrove.image.resize", "powertrove.image.compress", "powertrove.image.collage"], ["powertrove.data.merge-csv"]),
        new(["a.pdf"], "powertrove.pdf.compress", ["powertrove.pdf.extract-pages"], ["powertrove.pdf.merge", "powertrove.image.resize"]),
        new(["a.pdf", "b.pdf"], "powertrove.pdf.merge", ["powertrove.pdf.compress"], ["powertrove.image.resize"]),
        new(["a.jpg", "b.pdf"], "powertrove.pdf.stamp-selected-image", ["powertrove.pdf.combine", "powertrove.recipe.mixed-for-email"], ["powertrove.pdf.merge"]),
        new(["a.mp4"], "powertrove.video.compress", ["powertrove.video.extract-audio", "powertrove.media.trim"], ["powertrove.image.resize", "powertrove.pdf.compress"]),
        new(["a.mp3"], "powertrove.media.trim", ["powertrove.audio.convert-mp3"], ["powertrove.video.compress", "powertrove.video.remove-audio"]),
        new(["a.zip"], "powertrove.files.unzip", [], ["powertrove.image.resize", "powertrove.pdf.compress"]),
        new(["a.csv"], "powertrove.data.csv-to-xlsx", ["powertrove.data.csv-to-json"], ["powertrove.data.merge-csv", "powertrove.image.resize"]),
        new(["a.csv", "b.csv"], "powertrove.data.merge-csv", [], ["powertrove.image.resize"]),
        new(["a.json"], "powertrove.data.json-format", ["powertrove.data.json-to-csv", "powertrove.data.json-validate"], ["powertrove.data.csv-to-json"]),
        new(["a.txt", "b.txt"], "powertrove.data.merge-text", ["powertrove.files.compare-text"], ["powertrove.pdf.merge"]),
        // Single-family actions such as image resizing stay available for mixed selections: they process
        // their files and leave the others alone. Multi-file combinations of another family do not.
        new(["a.jpg", "b.mp4", "c.txt"], "powertrove.files.sort-by-type", ["powertrove.files.zip"], ["powertrove.pdf.merge", "powertrove.data.merge-csv"]),
    };

    public static IEnumerable<object[]> LocalizedCases =>
        from language in new[] { "en-US", "de-DE" }
        from culture in new[] { "en-US", "de-DE" }
        from row in Cases.Cast<object[]>()
        select new object[] { language, culture, row[0] };

    [Theory]
    [MemberData(nameof(LocalizedCases))]
    public async Task EmptyQuery_LeadsWithTheExpectedSuggestion(string language, string culture, Case expected)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(language);
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            // Build descriptors after selecting the language; never depend on the machine's locale.
            using var fixtures = new SelectionFixtures();
            var results = await new ActionSearchEngine(fixtures.Registry).SearchAsync("", fixtures.Explorer(expected.Files));
            var compatible = results.Where(r => r.IsCompatible).ToList();
            string shown = string.Join(", ", compatible.Take(8).Select(r => r.Action.Descriptor.Id));

            var best = Assert.Single(compatible, r => r.Highlight == ActionHighlight.BestFit);
            Assert.True(expected.BestFit == best.Action.Descriptor.Id, $"Best fit was {best.Action.Descriptor.Id}; first results: {shown}");
            Assert.Same(best, compatible[0]);
            var top = compatible.Take(6).Select(r => r.Action.Descriptor.Id).ToHashSet();
            foreach (var id in expected.NearTop)
                Assert.True(top.Contains(id), $"{id} is not among the first six; first results: {shown}");
            foreach (var id in expected.NotOffered)
                Assert.False(compatible.Any(r => r.Action.Descriptor.Id == id), $"{id} must not be offered for {expected}");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task EveryCase_ReferencesActionsThatExist()
    {
        // Guards the test set itself: a renamed action must fail here, not pass silently as "not offered".
        foreach (var expected in Cases.Cast<object[]>().Select(row => (Case)row[0]))
            foreach (var id in expected.NearTop.Append(expected.BestFit).Concat(expected.NotOffered))
                Assert.True(_fixtures.Registry.GetAction(id) is not null, $"Unknown action {id}");
        await Task.CompletedTask;
    }

    public void Dispose() => _fixtures.Dispose();
}
