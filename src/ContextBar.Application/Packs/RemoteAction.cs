using System.Collections.Frozen;
using ContextBar.Abstractions.Files;

namespace ContextBar.Application.Packs;

/// <summary>Normal IAction lifecycle with a worker transport; no engine instance is retained in the host.</summary>
public sealed class RemoteAction : IAction, IFileActionMetadata
{
    private readonly PackHostSupervisor _supervisor;
    private readonly PackWorkerGroup _group;
    private readonly FrozenSet<string>? _extensions;
    private readonly Func<CancellationToken, Task>? _confirmStarted;
    public ActionDescriptor Descriptor { get; }
    public IReadOnlyCollection<string>? InputExtensions => _extensions;
    public RemoteAction(PackHostSupervisor supervisor, PackWorkerGroup group, ActionDescriptor descriptor,
        IReadOnlyCollection<string>? extensions = null, Func<CancellationToken, Task>? confirmStarted = null)
    {
        if (!descriptor.Accepts.SetEquals(new[] { ContentKind.Files }))
            throw new ArgumentException("Worker actions must consume file selections.", nameof(descriptor));
        _supervisor = supervisor; _group = group; Descriptor = descriptor;
        _extensions = extensions?.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _confirmStarted = confirmStarted;
    }
    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var compatible = ActionContextCompatibility.Check(context, Descriptor);
        if (!compatible.IsAvailable) return new(compatible);
        if (context.Content is not FileSelection files) return new(new ActionAvailability(false, PackStrings.InvalidSelection));
        var eligible = files.Files.Where(f => _extensions is null || !f.IsDirectory && _extensions.Contains(f.Extension)).ToArray();
        if (eligible.Length == 0 || Descriptor.Selection is { AllowOtherItems: false } && eligible.Length != files.Files.Count
            || Descriptor.Selection is { } selection && SelectionRequirements.Evaluate(selection, context with { Content = new FileSelection(eligible) }) == SelectionFit.None)
            return new(new ActionAvailability(false, PackStrings.InvalidSelection));
        return new(new ActionAvailability(true));
    }
    public async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        var preview = await _supervisor.PreviewAsync(_group, Descriptor.Id, context, parameters, cancellationToken).ConfigureAwait(false);
        if (!preview.IsBlocked) await ConfirmAsync(cancellationToken).ConfigureAwait(false);
        return preview;
    }
    public async Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var receipt = await _supervisor.ExecuteAsync(_group, Descriptor.Id, context, parameters, progress, cancellationToken).ConfigureAwait(false);
        if (receipt.IsSuccess) await ConfirmAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }
    private async Task ConfirmAsync(CancellationToken token)
    {
        if (_confirmStarted is null) return;
        try { await _confirmStarted(token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        { /* Keep the previous version when confirmation/cleanup fails; never hide a completed execution. */ }
    }
}
