using System.Globalization;
using System.Text.RegularExpressions;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Core;
using PowerTrove.Core.Workflows;
using PowerTrove.Core.Search;
using PowerTrove.Application.Localization;
using PowerTrove.Desktop;
using PowerTrove.Pack.Images;
using PowerTrove.Pack.Media;

namespace PowerTrove.Architecture.Tests;

public sealed class LocalizedCatalogTests
{
    private static IReadOnlyList<IPackFactory> Factories(IWorkflowRunner runner) =>
        PackFactoriesTests.Factories().Concat(new IPackFactory[] { new RecipePackFactory(runner) }).ToArray();

    private static IReadOnlyList<IActionPack> CreatePacks()
    {
        var runner = new WorkflowRunner(new ActionRegistry());
        var host = PackFactoriesTests.Host()
            .Add(new CompositeImageEngine(new IImageEngine[] { new MetadataImageEngine() }))
            .Add(new CompositeMediaEngine(new IMediaEngine[] { new MetadataMediaEngine() }));
        return Factories(runner).Select(f => f.Create(host)).ToArray();
    }

    [Fact]
    public void EveryBuiltInAction_HasTranslatedMetadata_AndEnglishDoesNotLeakGerman()
    {
        var problems = LocalizedActionCatalog.FindProblems(CreatePacks);
        Assert.True(problems.Count == 0, "Catalog localization failures:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task EveryBuiltInEnglishActionTitle_IsDiscoverableRegardlessOfSelection()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            var packs = CreatePacks();
            var registry = new ActionRegistry();
            foreach (var pack in packs) registry.RegisterPack(pack.Info.Id, pack.Actions);
            var engine = new ActionSearchEngine(registry);
            var snapshot = new ContextSnapshot(Guid.NewGuid(),
                new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
                ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5), "test", null);

            foreach (var action in packs.SelectMany(pack => pack.Actions))
            {
                var hits = await engine.SearchAsync(action.Descriptor.Title, snapshot);
                Assert.Contains(hits, hit => hit.Action.Descriptor.Id == action.Descriptor.Id
                    && hit.TextMatchScore >= 100);
            }
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }

    [Fact]
    public async Task EverySupportedUiLanguage_FindsRepresentativeActionTitlesWithoutCompatibleSelection()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            var snapshot = new ContextSnapshot(Guid.NewGuid(),
                new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
                ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5), "test", null);

            foreach (string locale in UiLanguages.Supported)
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(locale);
                var packs = CreatePacks();
                var registry = new ActionRegistry();
                foreach (var pack in packs) registry.RegisterPack(pack.Info.Id, pack.Actions);
                var engine = new ActionSearchEngine(registry);
                // Representative title per pack; every locale is tested through the real registry and search engine.
                foreach (var pack in packs)
                {
                    var action = pack.Actions[0];
                    var results = await engine.SearchAsync(action.Descriptor.Title, snapshot);
                    Assert.Contains(results, r => r.Action.Descriptor.Id == action.Descriptor.Id
                        && r.TextMatchScore >= 100);
                }
            }
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }

    [Fact]
    public void FactorySet_MatchesRealDesktopComposition_IncludingHostRecipes()
    {
        string directory = Path.Combine(PackFactoriesTests.FindRoot(), "src", "PowerTrove.Desktop");
        string source = string.Join(Environment.NewLine,
            Directory.GetFiles(directory, "BuiltInActionPacks*.cs").OrderBy(p => p, StringComparer.Ordinal).Select(File.ReadAllText));
        var expected = Regex.Matches(source, @"new\s+(?<factory>\w+PackFactory)\s*\(")
            .Select(m => m.Groups["factory"].Value).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var actual = Factories(new WorkflowRunner(new ActionRegistry())).Select(f => f.GetType().Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
        var packs = CreatePacks();
        Assert.All(packs, p => Assert.NotEmpty(p.Actions));
        Assert.Contains(packs, p => p.Info.Id == "powertrove.recipes");
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("Description")]
    [InlineData("Parameters[Mode].Title")]
    [InlineData("Parameters[Mode].Choices[value].Title")]
    public void CheckerReportsEachUntranslatedField_WithTheActionId(string brokenField)
    {
        var previous = CultureInfo.CurrentUICulture;
        var problems = LocalizedActionCatalog.FindProblems(() => new IActionPack[] { new ControlPack(brokenField) });
        Assert.Contains("powertrove.test.localization." + brokenField + ": untranslated", problems);
        Assert.Equal(previous, CultureInfo.CurrentUICulture);
    }

    [Fact]
    public void CheckerRestoresCulture_WhenFactoryConstructionFails()
    {
        var previous = CultureInfo.CurrentUICulture;
        Assert.Throws<InvalidOperationException>(() => LocalizedActionCatalog.FindProblems(() => throw new InvalidOperationException("Control failure.")));
        Assert.Equal(previous, CultureInfo.CurrentUICulture);
    }

    private sealed class MetadataImageEngine : IImageEngine
    {
        public string Name => "metadata-only test engine";
        public bool CanDecode(string format) => true;
        public bool CanEncode(string format) => true;
        public bool CanPreserveMetadata => true;
        public Task TransformAsync(Stream source, Stream destination, ImageTransformRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Catalog enumeration must not execute an image transformation.");
    }

    private sealed class MetadataMediaEngine : IMediaEngine
    {
        public string Name => "metadata-only test engine";
        public bool IsAvailable => true;
        public bool Supports(MediaRequest request) => true;
        public Task ProcessAsync(string source, string destination, MediaRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Catalog enumeration must not execute a media transformation.");
    }

    private sealed class ControlPack : IActionPack
    {
        public ControlPack(string brokenField) => Actions = new IAction[] { new ControlAction(brokenField) };
        public ActionPackInfo Info { get; } = new("powertrove.test", "Test", "0.1.0", "Tests");
        public IReadOnlyList<IAction> Actions { get; }
    }

    private sealed class ControlAction(string brokenField) : IAction
    {
        private string Text(string field, string english, string german) => brokenField == field ? "Untranslated control" :
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? german : english;
        public ActionDescriptor Descriptor => new("powertrove.test.localization", Text("Title", "Control title", "Prüftitel"),
            Text("Description", "Control description", "Prüfbeschreibung"), new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(),
            RequiresPreview: false, ChangesFiles: false,
            Parameters: new[] { ActionParameterDefinition.Choice("Mode", Text("Parameters[Mode].Title", "Mode", "Modus"), "value",
                new[] { new ActionParameterChoice("value", Text("Parameters[Mode].Choices[value].Title", "Choice", "Auswahl")) }) });
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
