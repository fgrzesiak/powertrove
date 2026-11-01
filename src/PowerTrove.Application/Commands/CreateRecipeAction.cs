using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions;
using PowerTrove.Application.Resources;

namespace PowerTrove.Application.Commands;

/// <summary>
/// "Create recipe" in the palette. Context-free like <see cref="OpenSettingsAction"/>; the desktop host
/// opens the recipe editor when it is chosen, and executing it directly only invokes the callback.
/// </summary>
public sealed class CreateRecipeAction : IAction
{
    public const string ActionId = "powertrove.general.create-recipe";

    private readonly Action _open;

    public CreateRecipeAction(Action open)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        Descriptor = new ActionDescriptor(
            ActionId,
            Strings.Action_CreateRecipe_Title,
            Strings.Action_CreateRecipe_Description,
            new HashSet<ContentKind> { ContentKind.None },
            new[] { "recipe", "workflow", "automate", "chain", "steps" },
            RequiresPreview: false,
            ChangesFiles: false,
            ExplicitWeight: -10);
    }

    public ActionDescriptor Descriptor { get; }

    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ActionAvailability(true));
    }

    public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ActionPreview(false, Descriptor.Description, Array.Empty<string>(), Array.Empty<string>()));
    }

    public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new ActionReceipt(false, true, Array.Empty<string>(), Array.Empty<ActionFailure>(), false));
        _open();
        return Task.FromResult(new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false));
    }
}
