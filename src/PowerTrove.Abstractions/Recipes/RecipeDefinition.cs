using System.Collections.Generic;

namespace PowerTrove.Abstractions.Recipes;

/// <summary>
/// Defines a single step in a recipe, specifying which action to run and its parameters.
/// Family steps transform each matching file independently and retain other files.
/// Use one single-output transformation per family, followed by an unfiltered
/// aggregation step. A transformation that is skipped retains its original input.
/// </summary>
public sealed record RecipeStep(
    string ActionId,
    IReadOnlyDictionary<string, string> Parameters,
    string? Family = null);

/// <summary>
/// Defines a multi-step action that chains existing actions.
/// </summary>
public sealed record RecipeDefinition(
    string Id,
    string Title,
    string Description,
    SelectionRequirements Selection,
    IReadOnlyList<RecipeStep> Steps);
