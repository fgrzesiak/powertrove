using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Recipes;
using PowerTrove.Core.Workflows;

namespace PowerTrove.Core.Recipes;

/// <summary>
/// Executes a predefined multi-step recipe using the workflow runner.
/// Overrides the output directory to write intermediate outputs to a temporary folder,
/// so only the final step writes to the original target directory.
/// </summary>
public sealed class RecipeAction : IAction
{
    private readonly RecipeDefinition _recipe;
    private readonly IWorkflowRunner _workflowRunner;
    private readonly FileActionServices _services;
    private readonly WorkflowDefinition _workflow;

    public RecipeAction(RecipeDefinition recipe, IWorkflowRunner workflowRunner, FileActionServices services)
    {
        _recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        _workflowRunner = workflowRunner ?? throw new ArgumentNullException(nameof(workflowRunner));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        if (_recipe.Steps.Any(s => s.Family != null) &&
            (_recipe.Steps.Count < 2 || _recipe.Steps.Last().Family != null ||
             _recipe.Steps.Take(_recipe.Steps.Count - 1).Any(s => s.Family == null) ||
             _recipe.Steps.Take(_recipe.Steps.Count - 1).Select(s => s.Family).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _recipe.Steps.Count - 1))
            throw new ArgumentException("Family recipes require distinct family steps followed by one aggregation step.", nameof(recipe));

        var steps = _recipe.Steps.Select(step =>
            new WorkflowStep(step.ActionId, new DictionaryParameters(step.Parameters))).ToList();

        _workflow = new WorkflowDefinition(_recipe.Id, _recipe.Title, _recipe.Description, steps);

        Descriptor = new ActionDescriptor(
            Id: _recipe.Id,
            Title: _recipe.Title,
            Description: _recipe.Description,
            Accepts: new HashSet<ContentKind> { ContentKind.Files },
            SearchTerms: Array.Empty<string>(),
            RequiresPreview: true,
            ChangesFiles: true,
            RequiredCapabilities: null,
            ExplicitWeight: 0,
            Parameters: Array.Empty<ActionParameterDefinition>(),
            Selection: _recipe.Selection
        );
    }

    public ActionDescriptor Descriptor { get; }

    private bool HasFamilySteps => _recipe.Steps.Any(s => s.Family != null);

    private WorkflowDefinition SingleStep(int index) => _workflow with { Steps = new[] { _workflow.Steps[index] } };

    private static ContextSnapshot WithFiles(ContextSnapshot context, IEnumerable<SelectedFile> files) =>
        context with { Content = new FileSelection(files.ToArray()), ProvidedCapabilities = null };

    private static SelectedFile OutputFile(string path) =>
        new(path, Path.GetFileName(path), Path.GetExtension(path), null, null, true, true, null);

    private async ValueTask<ActionPreview> PreviewFamiliesAsync(ContextSnapshot context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.Content is not FileSelection selection || selection.Files.Count == 0)
            return new ActionPreview(false, PowerTrove.Abstractions.Resources.SdkStrings.Error_NotAFileSelection, Array.Empty<string>(), Array.Empty<string>(), true);

        var previews = new List<ActionPreview>();
        for (int i = 0; i < _recipe.Steps.Count - 1; i++)
        {
            ct.ThrowIfCancellationRequested();
            var files = selection.Files.Where(f => string.Equals(SelectionFamilies.Of(f), _recipe.Steps[i].Family, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (files.Length == 0) continue;
            previews.Add(await _workflowRunner.PreviewWorkflowAsync(SingleStep(i), WithFiles(context, files), ct).ConfigureAwait(false));
        }

        // Each branch replaces one input with one output (or passes it through if
        // skipped). Validate aggregation against the existing original selection:
        // no preview opens projected files that have not been created yet.
        var final = await _workflowRunner.PreviewWorkflowAsync(SingleStep(_workflow.Steps.Count - 1), context, ct).ConfigureAwait(false);
        return final with
        {
            RequiresConfirmation = final.RequiresConfirmation || previews.Any(p => p.RequiresConfirmation),
            PreviewText = string.Join("\n", previews.Select(p => p.PreviewText).Append(final.PreviewText)),
            Warnings = previews.SelectMany(p => p.Warnings).Concat(final.Warnings).ToArray(),
            IsBlocked = final.IsBlocked || previews.Any(p => p.IsBlocked)
        };
    }

    private async Task<ActionReceipt> ExecuteFamiliesAsync(ContextSnapshot context, IProgress<ActionProgress>? progress, CancellationToken ct)
    {
        var selection = (FileSelection)context.Content;
        var aggregate = new List<SelectedFile>();
        foreach (var file in selection.Files)
        {
            ct.ThrowIfCancellationRequested();
            int index = _recipe.Steps.ToList().FindIndex(s => s.Family != null && string.Equals(s.Family, SelectionFamilies.Of(file), StringComparison.OrdinalIgnoreCase));
            if (index < 0) { aggregate.Add(file); continue; }
            var receipt = await _workflowRunner.ExecuteWorkflowAsync(SingleStep(index), WithFiles(context, new[] { file }), progress, ct).ConfigureAwait(false);
            if (!receipt.IsSuccess || receipt.IsCanceled || receipt.Failures.Count > 0)
                return receipt with { IsSuccess = false, OutputPaths = Array.Empty<string>(), IsUndoable = false };
            // A skipped transformation must retain its original input in the archive.
            aggregate.AddRange(receipt.OutputPaths.Count == 0 ? new[] { file } : receipt.OutputPaths.Select(OutputFile));
        }

        _services.OutputDirectoryOverride.Value = Path.GetDirectoryName(selection.Files[0].Path);
        return await _workflowRunner.ExecuteWorkflowAsync(SingleStep(_workflow.Steps.Count - 1), WithFiles(context, aggregate), progress, ct).ConfigureAwait(false);
    }

    public async ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
    {
        if (HasFamilySteps)
        {
            var preview = await PreviewFamiliesAsync(context, cancellationToken).ConfigureAwait(false);
            return new ActionAvailability(!preview.IsBlocked, preview.IsBlocked ? preview.PreviewText : null);
        }
        if (_workflow.Steps.Count == 0)
        {
            return new ActionAvailability(false, PowerTrove.Core.Resources.Strings.RecipeNoSteps);
        }

        return await _workflowRunner.ValidateWorkflowAsync(_workflow, context, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        if (HasFamilySteps) return await PreviewFamiliesAsync(context, cancellationToken).ConfigureAwait(false);
        return await _workflowRunner.PreviewWorkflowAsync(_workflow, context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
    {
        if (HasFamilySteps && cancellationToken.IsCancellationRequested)
            return new ActionReceipt(false, true, Array.Empty<string>(), Array.Empty<ActionFailure>(), false);
        if (context.IsStale)
            return new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, PowerTrove.Core.Resources.Strings.ContextExpired) }, false);
        if (HasFamilySteps && (context.Content is not FileSelection files || files.Files.Count == 0))
            return new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, PowerTrove.Abstractions.Resources.SdkStrings.Error_NotAFileSelection) }, false);

        if (_workflow.Steps.Count == 0)
        {
            return new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false, PowerTrove.Core.Resources.Strings.NoStepsToExecute);
        }

        if (_workflow.Steps.Count == 1)
        {
            // Simple pass-through if only one step
            return await _workflowRunner.ExecuteWorkflowAsync(_workflow, context, progress, cancellationToken).ConfigureAwait(false);
        }

        string tempPath = Path.Combine(Path.GetTempPath(), "PowerTrove", "recipes", Guid.NewGuid().ToString("N"));
        _services.FileSystem.CreateDirectory(tempPath);

        try
        {
            if (HasFamilySteps)
            {
                _services.OutputDirectoryOverride.Value = tempPath;
                return await ExecuteFamiliesAsync(context, progress, cancellationToken).ConfigureAwait(false);
            }
            // Execute all but the last step with the temporary output directory override.
            var intermediateSteps = _workflow.Steps.Take(_workflow.Steps.Count - 1).ToList();
            var intermediateWorkflow = new WorkflowDefinition(_workflow.Id, _workflow.Title, _workflow.Description, intermediateSteps);

            _services.OutputDirectoryOverride.Value = tempPath;
            ActionReceipt intermediateReceipt;
            try
            {
                intermediateReceipt = await _workflowRunner.ExecuteWorkflowAsync(intermediateWorkflow, context, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _services.OutputDirectoryOverride.Value = null;
            }

            if (!intermediateReceipt.IsSuccess || intermediateReceipt.IsCanceled)
            {
                return new ActionReceipt(
                    intermediateReceipt.IsSuccess,
                    intermediateReceipt.IsCanceled,
                    Array.Empty<string>(), // We don't return intermediate outputs since they will be deleted.
                    intermediateReceipt.Failures,
                    false,
                    intermediateReceipt.Message
                );
            }

            // Create context for final step based on the outputs of the intermediate steps.
            var newFiles = intermediateReceipt.OutputPaths.Select(path =>
                new SelectedFile(path, Path.GetFileName(path) ?? "unknown", Path.GetExtension(path) ?? "", null, null, true, true, null)
            ).ToList();

            var finalContext = context with { Content = new FileSelection(newFiles) };

            // To make sure the final step writes next to the original files,
            // we override the output directory to the original folder of the first input file.
            if (context.Content is FileSelection originalSelection && originalSelection.Files.Count > 0)
            {
                var originalDir = Path.GetDirectoryName(originalSelection.Files[0].Path);
                if (!string.IsNullOrEmpty(originalDir))
                {
                    _services.OutputDirectoryOverride.Value = originalDir;
                }
            }

            var finalStep = _workflow.Steps.Last();
            var finalWorkflow = new WorkflowDefinition(_workflow.Id, _workflow.Title, _workflow.Description, new[] { finalStep });

            var finalReceipt = await _workflowRunner.ExecuteWorkflowAsync(finalWorkflow, finalContext, progress, cancellationToken).ConfigureAwait(false);

            return finalReceipt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ActionReceipt(false, true, Array.Empty<string>(), Array.Empty<ActionFailure>(), false);
        }
        finally
        {
            _services.OutputDirectoryOverride.Value = null; // Ensure it's cleared
            try
            {
                if (_services.FileSystem.DirectoryExists(tempPath))
                {
                    _services.FileSystem.DeleteDirectory(tempPath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
