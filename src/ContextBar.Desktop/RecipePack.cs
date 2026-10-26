using ContextBar.Core.Workflows;

using System;
using System.Collections.Generic;
using ContextBar.Abstractions;
using ContextBar.Abstractions.Files;
using ContextBar.Abstractions.Recipes;
using ContextBar.Core.Recipes;
using ContextBar.Abstractions.Packs;

namespace ContextBar.Desktop;

public sealed class RecipePack : IActionPack
{
    public RecipePack(IWorkflowRunner workflowRunner, FileActionServices services)
    {
        var recipe = new RecipeDefinition(
            "contextbar.recipe.images-for-email",
            UiText.RecipeImagesForEmailTitle,
            UiText.RecipeImagesForEmailDescription,
            SelectionRequirements.Multiple(new[] { "image" }),
            new List<RecipeStep>
            {
                new RecipeStep("contextbar.image.resize", new Dictionary<string, string> { { "MaxDimension", "1600" }, { "Enlarge", "false" } }),
                new RecipeStep("contextbar.image.compress", new Dictionary<string, string> { { "Quality", "75" } }),
                new RecipeStep("contextbar.files.zip", new Dictionary<string, string>())
            }
        );

        var recipe2 = new RecipeDefinition(
            "contextbar.recipe.mixed-for-email", UiText.RecipeMixedForEmailTitle, UiText.RecipeMixedForEmailDescription,
            SelectionRequirements.Multiple(new[] { "image", "pdf" }) with { AllowOtherItems = true },
            new[]
            {
                new RecipeStep("contextbar.image.compress", new Dictionary<string, string>
                {
                    ["MaxDimension"] = "1600", ["Quality"] = "75"
                }, Family: "image"),
                new RecipeStep("contextbar.pdf.compress", new Dictionary<string, string>
                {
                    ["Level"] = "balanced"
                }, Family: "pdf"),
                new RecipeStep("contextbar.files.zip", new Dictionary<string, string>())
            });

        Actions = new IAction[] { new RecipeAction(recipe, workflowRunner, services), new RecipeAction(recipe2, workflowRunner, services) };
    }

    public ActionPackInfo Info { get; } = Describe();

    internal static ActionPackInfo Describe() => new("contextbar.recipes", UiText.RecipePackTitle, "0.1.0", "PowerTrove");

    public IReadOnlyList<IAction> Actions { get; }
}

// Recipes are composed in the host and are not a separately distributable pack assembly.
internal sealed class RecipePackFactory(IWorkflowRunner runner) : IPackFactory
{
    public ActionPackInfo Info => RecipePack.Describe();
    public IActionPack Create(IPackHostServices host) => new RecipePack(runner, host.Files);
}
