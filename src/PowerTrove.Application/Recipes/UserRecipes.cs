using System;
using System.Collections.Generic;
using System.Linq;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Recipes;
using PowerTrove.Application.Configuration;
using PowerTrove.Application.Resources;
using PowerTrove.Core.Catalog;
using PowerTrove.Core.Recipes;
using PowerTrove.Core.Workflows;

namespace PowerTrove.Application.Recipes;

public enum RecipeProblem
{
    None,
    TitleMissing,
    TitleTooLong,
    TooFewSteps,
    TooManySteps,
    /// <summary>The step's action is not installed.</summary>
    ActionMissing,
    /// <summary>The step's action does not work on files.</summary>
    NotAFileAction,
    /// <summary>A step before the last creates no files the next step could use.</summary>
    StepCreatesNoFiles,
    /// <summary>Recipes cannot contain recipes.</summary>
    NestedRecipe
}

public sealed record RecipeValidation(RecipeProblem Problem, int StepIndex = -1)
{
    public bool IsValid => Problem == RecipeProblem.None;
}

/// <summary>
/// Recipes the user composes from installed file actions. Each step runs with the option values
/// stored in the recipe; intermediate files never stay next to the originals (see <see cref="RecipeAction"/>).
/// </summary>
public static class UserRecipes
{
    public const string PackId = "powertrove.user-recipes";
    public const string IdPrefix = "powertrove.user-recipe.";
    private const string BuiltInRecipePrefix = "powertrove.recipe.";
    public const int MaxTitleLength = 80;
    public const int MinSteps = 2;
    public const int MaxSteps = 8;
    public const int MaxRecipes = 50;

    public static string NewId() => IdPrefix + Guid.NewGuid().ToString("N")[..12];

    public static bool IsRecipe(string actionId) =>
        actionId.StartsWith(IdPrefix, StringComparison.Ordinal) || actionId.StartsWith(BuiltInRecipePrefix, StringComparison.Ordinal);

    /// <summary>Installed actions that can be a recipe step: file actions, not recipes or placeholders of missing packs.</summary>
    public static bool CanBeStep(IAction action) =>
        action is not CatalogAction && action.Descriptor.Accepts.Contains(ContentKind.Files) && !IsRecipe(action.Descriptor.Id);

    public static RecipeValidation Validate(string? title, IReadOnlyList<SavedRecipeStep> steps, Func<string, IAction?> resolve)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(resolve);
        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return new(RecipeProblem.TitleMissing);
        if (trimmed.Length > MaxTitleLength) return new(RecipeProblem.TitleTooLong);
        if (steps.Count < MinSteps) return new(RecipeProblem.TooFewSteps);
        if (steps.Count > MaxSteps) return new(RecipeProblem.TooManySteps);
        for (int i = 0; i < steps.Count; i++)
        {
            if (IsRecipe(steps[i].ActionId)) return new(RecipeProblem.NestedRecipe, i);
            var action = resolve(steps[i].ActionId);
            if (action is null or CatalogAction) return new(RecipeProblem.ActionMissing, i);
            if (!action.Descriptor.Accepts.Contains(ContentKind.Files)) return new(RecipeProblem.NotAFileAction, i);
            if (i < steps.Count - 1 && !action.Descriptor.ChangesFiles) return new(RecipeProblem.StepCreatesNoFiles, i);
        }
        return new(RecipeProblem.None);
    }

    /// <summary>Drops malformed entries from settings and keeps at most <see cref="MaxRecipes"/> recipes.</summary>
    public static IReadOnlyList<SavedRecipe>? Normalize(IEnumerable<SavedRecipe?>? recipes)
    {
        if (recipes is null) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SavedRecipe>();
        foreach (var recipe in recipes)
        {
            if (recipe is null || string.IsNullOrWhiteSpace(recipe.Title) || recipe.Id is null
                || !recipe.Id.StartsWith(IdPrefix, StringComparison.Ordinal) || !seen.Add(recipe.Id))
                continue;
            var steps = (recipe.Steps ?? [])
                .Where(step => step is not null && !string.IsNullOrWhiteSpace(step.ActionId))
                .Select(step => new SavedRecipeStep(step.ActionId,
                    new Dictionary<string, string>(step.Parameters ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)))
                .Take(MaxSteps)
                .ToArray();
            if (steps.Length == 0) continue;
            string title = recipe.Title.Trim();
            result.Add(new SavedRecipe(recipe.Id, title.Length > MaxTitleLength ? title[..MaxTitleLength] : title, steps));
            if (result.Count == MaxRecipes) break;
        }
        return result;
    }

    /// <summary>The palette action for a saved recipe. It takes the selection shape of its first step.</summary>
    public static IAction ToAction(SavedRecipe recipe, Func<string, IAction?> resolve, IWorkflowRunner runner, FileActionServices services)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(resolve);
        var first = recipe.Steps.Count > 0 ? resolve(recipe.Steps[0].ActionId) : null;
        var titles = recipe.Steps.Select(step => resolve(step.ActionId)?.Descriptor.Title ?? step.ActionId);
        var definition = new RecipeDefinition(
            recipe.Id,
            recipe.Title,
            Strings.UserRecipeDescription(string.Join(" → ", titles)),
            first?.Descriptor.Selection ?? SelectionRequirements.Any(),
            recipe.Steps.Select(step => new RecipeStep(step.ActionId, step.Parameters)).ToArray());
        return new RecipeAction(definition, runner, services);
    }
}
