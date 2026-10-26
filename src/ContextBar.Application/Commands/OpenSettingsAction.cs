using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions;
using ContextBar.Application.Resources;

namespace ContextBar.Application.Commands;

/// <summary>
/// "Settings" in the palette. Context-free (accepts <see cref="ContentKind.None"/>), so it is available
/// for every selection and when context capture failed. The desktop host performs the command when
/// this action is chosen; executing it directly only invokes the supplied callback.
/// </summary>
public sealed class OpenSettingsAction : IAction
{
    public const string ActionId = "contextbar.general.settings";

    private readonly Action _open;

    public OpenSettingsAction(Action open)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        Descriptor = new ActionDescriptor(
            ActionId,
            Strings.Action_OpenSettings_Title,
            Strings.Action_OpenSettings_Description,
            new HashSet<ContentKind> { ContentKind.None },
            new[] { "settings", "preferences", "options", "config", "language", "shortcut" },
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
