using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Core.Workflows;

/// <summary>
/// A step within a workflow that refers to a registered IAction and parameter bindings.
/// </summary>
public sealed record WorkflowStep(
    string ActionId,
    ActionParameters Parameters,
    string? Description = null);

/// <summary>
/// Represents a declarative sequence of steps.
/// </summary>
public sealed record WorkflowDefinition(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<WorkflowStep> Steps);

/// <summary>
/// Runner interface for executing workflows securely in offline-only, typed fashion.
/// </summary>
public interface IWorkflowRunner
{
    ValueTask<ActionAvailability> ValidateWorkflowAsync(WorkflowDefinition workflow, ContextSnapshot context, CancellationToken cancellationToken = default);
    ValueTask<ActionPreview> PreviewWorkflowAsync(WorkflowDefinition workflow, ContextSnapshot context, CancellationToken cancellationToken = default);
    Task<ActionReceipt> ExecuteWorkflowAsync(WorkflowDefinition workflow, ContextSnapshot context, IProgress<ActionProgress>? progress, CancellationToken cancellationToken = default);
}
