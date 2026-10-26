using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Abstractions;

/// <summary>
/// Optional planning contract for actions used after the first workflow step.
/// The context describes projected outputs, which do not exist yet. Implementations
/// must validate availability and parameters without opening files or performing
/// side effects, and return a blocked preview when validation cannot be completed.
/// ExpectedOutputs must describe the files passed to the next step; an empty list
/// preserves the input context, matching workflow execution semantics.
/// </summary>
public interface IWorkflowStepPlanner
{
    ValueTask<ActionPreview> PlanAsync(
        ContextSnapshot projectedContext,
        ActionParameters parameters,
        CancellationToken cancellationToken);
}
