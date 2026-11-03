using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions;

namespace PowerTrove.Application.Commands;

/// <summary>
/// "Connect browser" in the palette. Offered only while the desktop host says the browser setup is
/// missing (browser source, Browser pack installed, no pairing). The host opens the Browser section of
/// Settings when it is chosen; executing it directly only invokes the callback.
/// </summary>
public sealed class ConnectBrowserAction : IAction
{
    public const string ActionId = "powertrove.general.connect-browser";

    private readonly Func<ContextSnapshot, CancellationToken, ValueTask<bool>> _needed;
    private readonly Action _open;

    /// <param name="searchTerms">Localized synonyms, separated by spaces.</param>
    public ConnectBrowserAction(string title, string description, string searchTerms,
        Func<ContextSnapshot, CancellationToken, ValueTask<bool>> needed, Action open)
    {
        _needed = needed ?? throw new ArgumentNullException(nameof(needed));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        var terms = new List<string> { "browser", "chrome", "edge", "extension", "connect" };
        foreach (var term in (searchTerms ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (!terms.Contains(term, StringComparer.OrdinalIgnoreCase)) terms.Add(term);
        Descriptor = new ActionDescriptor(
            ActionId,
            title,
            description,
            new HashSet<ContentKind> { ContentKind.None },
            terms,
            RequiresPreview: false,
            ChangesFiles: false,
            // Shown only when the browser setup is missing; then it is the most useful first entry.
            ExplicitWeight: 160);
    }

    public ActionDescriptor Descriptor { get; }

    public async ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ActionAvailability(await _needed(context, cancellationToken).ConfigureAwait(false));
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
