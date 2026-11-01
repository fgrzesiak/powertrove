using System.Collections.Frozen;
using System.Text.Json;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Application.Configuration;
using PowerTrove.Application.Diagnostics;
using PowerTrove.Application.Recipes;
using PowerTrove.Core;
using PowerTrove.Core.Catalog;
using PowerTrove.Core.Diagnostics;
using PowerTrove.Core.Workflows;

namespace PowerTrove.Application.Tests;

public sealed class UserRecipesTests
{
    private sealed class StepAction(string id, bool changesFiles = true, ContentKind kind = ContentKind.Files, SelectionRequirements? selection = null) : IAction
    {
        public ActionDescriptor Descriptor { get; } = new(id, "Title " + id, "", new[] { kind }.ToFrozenSet(), [], true, changesFiles,
            Selection: selection);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) =>
            new(new ActionPreview(false, "", [], []));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new ActionReceipt(true, false, [], [], false));
    }

    private static readonly Dictionary<string, IAction> Actions = new IAction[]
    {
        new StepAction("resize", selection: SelectionRequirements.Any("image")),
        new StepAction("zip"),
        new StepAction("details", changesFiles: false),
        new StepAction("table", kind: ContentKind.Table)
    }.ToDictionary(a => a.Descriptor.Id);

    private static IAction? Resolve(string id) => Actions.GetValueOrDefault(id);

    private static SavedRecipeStep Step(string id) => new(id, new Dictionary<string, string>());

    [Theory]
    [InlineData("", RecipeProblem.TitleMissing, -1)]
    [InlineData("   ", RecipeProblem.TitleMissing, -1)]
    public void Validate_RequiresTitle(string title, RecipeProblem problem, int step)
    {
        Assert.Equal(new RecipeValidation(problem, step), UserRecipes.Validate(title, [Step("resize"), Step("zip")], Resolve));
    }

    [Fact]
    public void Validate_ChecksLengthAndStepCount()
    {
        Assert.Equal(RecipeProblem.TitleTooLong, UserRecipes.Validate(new string('x', UserRecipes.MaxTitleLength + 1), [Step("resize"), Step("zip")], Resolve).Problem);
        Assert.Equal(RecipeProblem.TooFewSteps, UserRecipes.Validate("Web", [Step("resize")], Resolve).Problem);
        Assert.Equal(RecipeProblem.TooManySteps, UserRecipes.Validate("Web", Enumerable.Repeat(Step("resize"), UserRecipes.MaxSteps + 1).ToArray(), Resolve).Problem);
        Assert.True(UserRecipes.Validate("Web", [Step("resize"), Step("resize"), Step("zip")], Resolve).IsValid);
    }

    [Fact]
    public void Validate_NamesTheFirstUnusableStep()
    {
        Assert.Equal(new RecipeValidation(RecipeProblem.ActionMissing, 1), UserRecipes.Validate("Web", [Step("resize"), Step("gone")], Resolve));
        Assert.Equal(new RecipeValidation(RecipeProblem.NotAFileAction, 0), UserRecipes.Validate("Web", [Step("table"), Step("zip")], Resolve));
        Assert.Equal(new RecipeValidation(RecipeProblem.StepCreatesNoFiles, 0), UserRecipes.Validate("Web", [Step("details"), Step("zip")], Resolve));
        Assert.Equal(new RecipeValidation(RecipeProblem.NestedRecipe, 1), UserRecipes.Validate("Web", [Step("resize"), Step("powertrove.recipe.images-for-email")], Resolve));
        // A step that creates no files is fine at the end.
        Assert.True(UserRecipes.Validate("Web", [Step("resize"), Step("details")], Resolve).IsValid);
    }

    [Fact]
    public void CanBeStep_ExcludesRecipesPlaceholdersAndNonFileActions()
    {
        Assert.True(UserRecipes.CanBeStep(Actions["resize"]));
        Assert.False(UserRecipes.CanBeStep(Actions["table"]));
        Assert.False(UserRecipes.CanBeStep(new StepAction(UserRecipes.IdPrefix + "abc")));
        Assert.False(UserRecipes.CanBeStep(new StepAction("powertrove.recipe.mixed-for-email")));
    }

    [Fact]
    public void Normalize_DropsMalformedAndDuplicateRecipes()
    {
        var good = new SavedRecipe(UserRecipes.IdPrefix + "a", "  Web  ", [Step("resize"), Step("zip")]);
        var normalized = UserRecipes.Normalize(new SavedRecipe?[]
        {
            good,
            good with { Title = "Duplicate" },
            null,
            new("foreign.id", "Foreign", [Step("zip")]),
            new(UserRecipes.IdPrefix + "b", "", [Step("zip")]),
            new(UserRecipes.IdPrefix + "c", "Empty", []),
        })!;

        var recipe = Assert.Single(normalized);
        Assert.Equal("Web", recipe.Title);
        Assert.Equal(["resize", "zip"], recipe.Steps.Select(s => s.ActionId));
        Assert.Null(UserRecipes.Normalize(null));
    }

    [Fact]
    public void ToAction_UsesFirstStepSelectionAndStableId()
    {
        var recipe = new SavedRecipe(UserRecipes.IdPrefix + "a", "Web", [Step("resize"), Step("zip")]);
        var action = UserRecipes.ToAction(recipe, Resolve, new WorkflowRunner(new ActionRegistry()),
            FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()));

        Assert.Equal(recipe.Id, action.Descriptor.Id);
        Assert.Equal("Web", action.Descriptor.Title);
        Assert.Equal(Actions["resize"].Descriptor.Selection, action.Descriptor.Selection);
        Assert.Contains("Title resize", action.Descriptor.Description);
        Assert.True(action.Descriptor.ChangesFiles);
    }

    [Fact]
    public void Settings_PersistRecipesAndKeepThemOnReset()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PowerTrove-recipes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var recipe = new SavedRecipe(UserRecipes.NewId(), "Web", [new SavedRecipeStep("resize", new() { ["MaxDimension"] = "1600" }), Step("zip")]);
            var settings = new SettingsService(path);
            settings.SaveUserRecipe(recipe);
            settings.SaveUserRecipe(recipe with { Title = "Website" });

            settings = new SettingsService(path);
            var saved = Assert.Single(settings.UserRecipes);
            Assert.Equal("Website", saved.Title);
            Assert.Equal("1600", saved.Steps[0].Parameters["maxdimension"]);

            settings.SetUiLanguage("de");
            settings.ResetToDefaults();
            Assert.Null(settings.Settings.UiLanguage);
            Assert.Single(settings.UserRecipes);

            settings.RemoveUserRecipe(recipe.Id);
            Assert.Empty(new SettingsService(path).UserRecipes);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Settings_RememberDismissedLauncherHint_AndOlderFilesDefaultToShowingIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PowerTrove-hotkey-hint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(UserSettings.CreateDefault()).Replace(",\"LauncherHotkeyHintDismissed\":false", ""));
            var settings = new SettingsService(path);
            Assert.False(settings.LauncherHotkeyHintDismissed);
            settings.DismissLauncherHotkeyHint();
            Assert.True(new SettingsService(path).LauncherHotkeyHintDismissed);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DiagnosticsReport_ContainsVersionsCountsAndTimingsOnly()
    {
        var report = DiagnosticsReport.Build(new DiagnosticsInfo("0.3.2-alpha.4", "10.0.26200.0", "X64", "unpackaged", 149, 120L * 1024 * 1024,
            [new MetricSummary("palette.shown", 12, 41.25, 88, 120.5), new MetricSummary("context.resolve / powertrove.provider.explorer", 3, 20, 25, 25)]));

        Assert.Contains("Version: 0.3.2-alpha.4 (unpackaged)", report);
        Assert.Contains("Actions: 149", report);
        Assert.Contains("Memory: 120 MB", report);
        Assert.Contains("palette.shown: 12 / 41.3 / 88.0 / 120.5", report);
        Assert.Contains("context.resolve / powertrove.provider.explorer: 3 / 20.0 / 25.0 / 25.0", report);
        Assert.DoesNotContain(":\\", report);
        Assert.Contains("none yet", DiagnosticsReport.Build(new DiagnosticsInfo("1", "10", "X64", "unpackaged", 0, 0, [])));
    }
}
