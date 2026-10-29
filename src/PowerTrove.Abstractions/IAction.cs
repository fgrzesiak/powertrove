using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Abstractions;

public sealed record ActionDescriptor(
    string Id,
    string Title,
    string Description,
    IReadOnlySet<ContentKind> Accepts,
    IReadOnlyList<string> SearchTerms,
    bool RequiresPreview,
    bool ChangesFiles,
    IReadOnlySet<string>? RequiredCapabilities = null,
    int ExplicitWeight = 0,
    IReadOnlyList<ActionParameterDefinition>? Parameters = null,
    SelectionRequirements? Selection = null,
    bool CanRememberParameters = true);

public sealed record ActionAvailability(
    bool IsAvailable,
    string? Reason = null);

public abstract record ActionParameters;
public sealed record EmptyParameters() : ActionParameters;
/// <summary>
/// String-keyed parameter values, as entered in the options dialog or stored in
/// preferences. Actions read them through <see cref="ActionParameterValues"/>,
/// which validates against the declared <see cref="ActionParameterDefinition"/>s.
/// </summary>
public sealed record DictionaryParameters(IReadOnlyDictionary<string, string> Values) : ActionParameters;

/// <param name="IsBlocked">
/// True when the action cannot run with the current selection or options
/// (for example an invalid page range). The host must not offer execution.
/// </param>
public sealed record ActionPreview(
    bool RequiresConfirmation,
    string? PreviewText,
    IReadOnlyList<string> ExpectedOutputs,
    IReadOnlyList<string> Warnings,
    bool IsBlocked = false,
    IReadOnlyList<RenamePreviewRow>? RenameRows = null,
    ActionConfirmation? Confirmation = null,
    IReadOnlyList<ActionParameterChoiceSet>? ParameterChoices = null)
{
    // Non-positional to preserve the constructor used by already published packs.
    public ImagePreviewData? ImagePreview { get; init; }
}

/// <summary>Input-derived labels for an options dialog only. Never log or persist the discovered list.</summary>
public sealed record ActionParameterChoiceSet(string ParameterName, string ContextKey,
    IReadOnlyList<ActionParameterChoice> Choices, string? Description = null)
{
    public const int MaximumChoices = 256;
    public const int MaximumChoiceLength = 256;
    public const int MaximumSelectionLength = 4096;
    public override string ToString() => "ActionParameterChoiceSet { Payload = redacted }";
}

/// <summary>Sensitive names: render only inside the options dialog; never persist or log.</summary>
public sealed record RenamePreviewRow(string OldName, string NewName);
public sealed record ActionConfirmation(string Prompt, string ParameterName, string Token);

public sealed record ActionFailure(
    string? Item,
    string ErrorMessage);

public sealed record ActionReceipt(
    bool IsSuccess,
    bool IsCanceled,
    IReadOnlyList<string> OutputPaths,
    IReadOnlyList<ActionFailure> Failures,
    bool IsUndoable,
    string? Message = null,
    string? ClipboardText = null,
    Guid? UndoOperationId = null,
    DateTimeOffset? UndoUntil = null);

/// <summary>
/// Reports intermediate progress during action execution.
/// </summary>
/// <param name="Percentage">A fractional value between 0.0 and 1.0 representing overall progress.</param>
/// <param name="CurrentItem">Optional name of the item currently being processed.</param>
/// <param name="StatusMessage">Optional descriptive message of the current operation.</param>
public sealed record ActionProgress(
    double Percentage,
    string? CurrentItem = null,
    string? StatusMessage = null);

public interface IAction
{
    ActionDescriptor Descriptor { get; }
    ValueTask<ActionAvailability> MatchAsync(
        ContextSnapshot context,
        CancellationToken cancellationToken);
    ValueTask<ActionPreview> PreviewAsync(
        ContextSnapshot context,
        ActionParameters parameters,
        CancellationToken cancellationToken);
    Task<ActionReceipt> ExecuteAsync(
        ContextSnapshot context,
        ActionParameters parameters,
        IProgress<ActionProgress>? progress,
        CancellationToken cancellationToken);
}
