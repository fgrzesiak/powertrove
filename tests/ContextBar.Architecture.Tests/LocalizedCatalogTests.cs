using System.Globalization;
using System.Text.RegularExpressions;
using ContextBar.Abstractions.Files;
using ContextBar.Abstractions.Packs;
using ContextBar.Abstractions.Testing;
using ContextBar.Core;
using ContextBar.Core.Workflows;
using ContextBar.Desktop;
using ContextBar.Pack.Images;
using ContextBar.Pack.Media;

namespace ContextBar.Architecture.Tests;

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
    public void FactorySet_MatchesRealDesktopComposition_IncludingHostRecipes()
    {
        string directory = Path.Combine(PackFactoriesTests.FindRoot(), "src", "ContextBar.Desktop");
        string source = string.Join(Environment.NewLine,
            Directory.GetFiles(directory, "BuiltInActionPacks*.cs").OrderBy(p => p, StringComparer.Ordinal).Select(File.ReadAllText));
        var expected = Regex.Matches(source, @"new\s+(?<factory>\w+PackFactory)\s*\(")
            .Select(m => m.Groups["factory"].Value).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var actual = Factories(new WorkflowRunner(new ActionRegistry())).Select(f => f.GetType().Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
        var packs = CreatePacks();
        Assert.All(packs, p => Assert.NotEmpty(p.Actions));
        Assert.Contains(packs, p => p.Info.Id == "contextbar.recipes");
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
        Assert.Contains("contextbar.test.localization." + brokenField + ": untranslated", problems);
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
        public ActionPackInfo Info { get; } = new("contextbar.test", "Test", "0.1.0", "Tests");
        public IReadOnlyList<IAction> Actions { get; }
    }

    private sealed class ControlAction(string brokenField) : IAction
    {
        private string Text(string field, string english, string german) => brokenField == field ? "Untranslated control" :
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? german : english;
        public ActionDescriptor Descriptor => new("contextbar.test.localization", Text("Title", "Control title", "Prüftitel"),
            Text("Description", "Control description", "Prüfbeschreibung"), new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(),
            RequiresPreview: false, ChangesFiles: false,
            Parameters: new[] { ActionParameterDefinition.Choice("Mode", Text("Parameters[Mode].Title", "Mode", "Modus"), "value",
                new[] { new ActionParameterChoice("value", Text("Parameters[Mode].Choices[value].Title", "Choice", "Auswahl")) }) });
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
