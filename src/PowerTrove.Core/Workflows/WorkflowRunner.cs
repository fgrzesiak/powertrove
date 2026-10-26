using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions.Files;

namespace PowerTrove.Core.Workflows;

public class WorkflowRunner : IWorkflowRunner
{
    private static readonly System.Resources.ResourceManager Resources =
        new("PowerTrove.Core.Resources.Strings", typeof(WorkflowRunner).Assembly);
    private readonly IActionRegistry _actionRegistry;

    public WorkflowRunner(IActionRegistry actionRegistry)
    {
        _actionRegistry = actionRegistry ?? throw new ArgumentNullException(nameof(actionRegistry));
    }

    public async ValueTask<ActionAvailability> ValidateWorkflowAsync(WorkflowDefinition workflow, ContextSnapshot context, CancellationToken cancellationToken = default)
    {
        if (workflow == null) throw new ArgumentNullException(nameof(workflow));
        if (context == null) throw new ArgumentNullException(nameof(context));

        if (workflow.Steps.Count == 0)
        {
            return new ActionAvailability(false, PowerTrove.Core.Resources.Strings.WorkflowNoSteps);
        }

        var firstStep = workflow.Steps[0];
        var action = _actionRegistry.GetAction(firstStep.ActionId);
        if (action == null)
        {
            return new ActionAvailability(false, $"Action '{firstStep.ActionId}' not found in registry.");
        }

        var availability = await action.MatchAsync(context, cancellationToken).ConfigureAwait(false);
        if (!availability.IsAvailable)
        {
            return new ActionAvailability(false, $"First step '{firstStep.ActionId}' is incompatible with the initial context: {availability.Reason}");
        }

        if (workflow.Steps.Count > 1)
        {
            var preview = await PreviewWorkflowAsync(workflow, context, cancellationToken).ConfigureAwait(false);
            if (preview.IsBlocked)
                return new ActionAvailability(false, string.Join("\n", preview.Warnings));
        }

        return new ActionAvailability(true);
    }

    public async ValueTask<ActionPreview> PreviewWorkflowAsync(WorkflowDefinition workflow, ContextSnapshot context, CancellationToken cancellationToken = default)
    {
        if (workflow == null) throw new ArgumentNullException(nameof(workflow));
        if (context == null) throw new ArgumentNullException(nameof(context));

        if (workflow.Steps.Count == 0)
        {
            return new ActionPreview(false, "No steps.", Array.Empty<string>(), Array.Empty<string>(), true);
        }

        var expectedOutputs = new List<string>();
        var warnings = new List<string>();
        bool requiresConfirmation = false;
        var stepDescriptions = new List<string>();
        bool isBlocked = false;
        var projectedContext = context;

        for (int i = 0; i < workflow.Steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = workflow.Steps[i];
            var action = _actionRegistry.GetAction(step.ActionId);

            if (action == null)
            {
                warnings.Add($"Action '{step.ActionId}' is missing.");
                return new ActionPreview(false, $"Failed to resolve action '{step.ActionId}'.", Array.Empty<string>(), warnings, true);
            }

            if (i == 0)
            {
                var availability = await action.MatchAsync(context, cancellationToken).ConfigureAwait(false);
                if (!availability.IsAvailable)
                {
                    warnings.Add(availability.Reason ?? "Unknown reason");
                    return new ActionPreview(false, $"Incompatible step: {step.ActionId}", Array.Empty<string>(), warnings, true);
                }

                var preview = await action.PreviewAsync(context, step.Parameters, cancellationToken).ConfigureAwait(false);

                if (preview.IsBlocked)
                {
                    isBlocked = true;
                }

                requiresConfirmation |= preview.RequiresConfirmation;
                expectedOutputs.AddRange(preview.ExpectedOutputs);
                warnings.AddRange(preview.Warnings);

                if (!string.IsNullOrEmpty(preview.PreviewText))
                {
                    stepDescriptions.Add($"[{action.Descriptor.Title}] {preview.PreviewText}");
                }
                else
                {
                    stepDescriptions.Add($"[{action.Descriptor.Title}]");
                }
            }
            else
            {
                var compatibility = ActionContextCompatibility.Check(projectedContext, action.Descriptor);
                if (!compatibility.IsAvailable)
                {
                    warnings.Add(compatibility.Reason!);
                    isBlocked = true;
                    break;
                }

                // MatchAsync/PreviewAsync may open files. Only an explicit planning
                // contract may inspect metadata for intermediate, nonexistent outputs.
                if (action is not IWorkflowStepPlanner planner)
                {
                    warnings.Add(Resources.GetString("Workflow_PlanningUnavailable", System.Globalization.CultureInfo.CurrentUICulture)!);
                    isBlocked = true;
                    break;
                }

                var plan = await planner.PlanAsync(projectedContext, step.Parameters, cancellationToken).ConfigureAwait(false);
                requiresConfirmation |= plan.RequiresConfirmation;
                warnings.AddRange(plan.Warnings);
                stepDescriptions.Add($"[{action.Descriptor.Title}] {plan.PreviewText}");
                isBlocked |= plan.IsBlocked;
                if (isBlocked) break;
                if (plan.ExpectedOutputs.Count > 0)
                {
                    projectedContext = ProjectOutputs(projectedContext, plan.ExpectedOutputs);
                }
            }

            if (isBlocked) break;
            if (i == 0 && expectedOutputs.Count > 0)
            {
                projectedContext = ProjectOutputs(context, expectedOutputs);
            }
        }

        string overallPreview = PowerTrove.Core.Resources.Strings.WorkflowPreviewTitle(workflow.Title) + "\n" + string.Join("\n", stepDescriptions);

        return new ActionPreview(
            RequiresConfirmation: requiresConfirmation,
            PreviewText: overallPreview,
            ExpectedOutputs: expectedOutputs.Distinct().ToList(),
            Warnings: warnings,
            IsBlocked: isBlocked
        );
    }

    private static ContextSnapshot ProjectOutputs(ContextSnapshot context, IReadOnlyList<string> paths) =>
        context with
        {
            Content = new FileSelection(paths.Select(path => new SelectedFile(
                path, System.IO.Path.GetFileName(path), System.IO.Path.GetExtension(path),
                null, null, true, true, null)).ToList()),
            // Captured application capabilities do not transfer to generated files.
            ProvidedCapabilities = null
        };

    public async Task<ActionReceipt> ExecuteWorkflowAsync(WorkflowDefinition workflow, ContextSnapshot context, IProgress<ActionProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (workflow == null) throw new ArgumentNullException(nameof(workflow));
        if (context == null) throw new ArgumentNullException(nameof(context));

        var currentOutputPaths = new List<string>();
        var overallFailures = new List<ActionFailure>();
        bool isCanceled = false;
        var currentContext = context;
        int stepIndex = 0;
        string? lastClipboardText = null;
        string? lastMessage = null;

        foreach (var step in workflow.Steps)
        {
            stepIndex++;
            progress?.Report(new ActionProgress((double)stepIndex / (workflow.Steps.Count + 1), PowerTrove.Core.Resources.Strings.WorkflowStepStarting(step.ActionId), null));

            var action = _actionRegistry.GetAction(step.ActionId);
            if (action == null)
            {
                overallFailures.Add(new ActionFailure(null, PowerTrove.Core.Resources.Strings.WorkflowActionNotFound(step.ActionId)));
                break;
            }

            var stepProgress = new Progress<ActionProgress>(p =>
            {
                double stepBase = (double)(stepIndex - 1) / workflow.Steps.Count;
                double stepContribution = p.Percentage / workflow.Steps.Count;
                progress?.Report(new ActionProgress(stepBase + stepContribution, p.CurrentItem, p.StatusMessage));
            });

            var receipt = await action.ExecuteAsync(currentContext, step.Parameters, stepProgress, cancellationToken).ConfigureAwait(false);

            currentOutputPaths = receipt.OutputPaths.ToList();
            overallFailures.AddRange(receipt.Failures);
            lastClipboardText = receipt.ClipboardText;
            lastMessage = receipt.Message;

            if (receipt.IsCanceled)
            {
                isCanceled = true;
                break;
            }

            if (!receipt.IsSuccess || receipt.Failures.Count > 0)
            {
                break;
            }

            if (receipt.OutputPaths.Count > 0)
            {
                var newFiles = receipt.OutputPaths.Select(path =>
                    new SelectedFile(path, System.IO.Path.GetFileName(path) ?? "unknown", System.IO.Path.GetExtension(path) ?? "", null, null, true, true, null)
                ).ToList();

                currentContext = currentContext with { Content = new FileSelection(newFiles) };
            }
        }

        progress?.Report(new ActionProgress(1.0, PowerTrove.Core.Resources.Strings.WorkflowComplete, null));

        bool isSuccess = overallFailures.Count == 0 && !isCanceled;

        return new ActionReceipt(
            IsSuccess: isSuccess,
            IsCanceled: isCanceled,
            OutputPaths: currentOutputPaths.Distinct().ToList(),
            Failures: overallFailures,
            IsUndoable: false,
            Message: isSuccess ? (lastMessage ?? PowerTrove.Core.Resources.Strings.WorkflowCompleted(workflow.Title)) : PowerTrove.Core.Resources.Strings.WorkflowFailed(workflow.Title),
            ClipboardText: lastClipboardText
        );
    }
}
