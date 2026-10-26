using ContextBar.Application.ViewModels;
using ContextBar.Core.Search;

namespace ContextBar.Application.Search;

public enum PaletteEnterOutcome { Ignored, Retried, Executed }

/// <summary>
/// UI-thread interaction gate shared by Enter and the Retry button. Capture intent before awaiting
/// search; retrying never falls through to execution. Invalidate when the palette hides or intent changes.
/// </summary>
public sealed class PaletteEnterController
{
    private long _version;
    public bool IsPending { get; private set; }

    public void Invalidate()
    {
        _version++;
        IsPending = false;
    }

    public async Task<PaletteEnterOutcome> HandleEnterAsync(PaletteViewModel viewModel,
        Func<ActionSearchResult, Task> execute, Func<bool> canContinue)
    {
        if (IsPending || viewModel.IsExecuting || viewModel.HasExecutionResult || viewModel.ShowFirstRun || !canContinue())
            return PaletteEnterOutcome.Ignored;
        long version = ++_version;
        IsPending = true;
        // SearchError clears while a retry is running: preserve the user's original retry intent.
        bool retry = viewModel.SearchError != null;
        var search = viewModel.SearchTask;
        var query = viewModel.SearchText;
        var invocation = viewModel.CurrentSnapshot?.InvocationId;
        var selection = viewModel.SelectionRevision;
        bool Current() => version == _version && query == viewModel.SearchText &&
            invocation == viewModel.CurrentSnapshot?.InvocationId && selection == viewModel.SelectionRevision &&
            !viewModel.IsExecuting && !viewModel.HasExecutionResult && !viewModel.ShowFirstRun && canContinue();
        try
        {
            await search;
            if (!Current() || !ReferenceEquals(search, viewModel.SearchTask)) return PaletteEnterOutcome.Ignored;
            if (retry)
            {
                var retryTask = viewModel.RetrySearchAsync();
                await retryTask;
                return Current() && ReferenceEquals(retryTask, viewModel.SearchTask)
                    ? PaletteEnterOutcome.Retried : PaletteEnterOutcome.Ignored;
            }
            if (viewModel.SearchError != null || viewModel.SelectedAction is not { IsCompatible: true } result)
                return PaletteEnterOutcome.Ignored;
            await execute(result);
            return PaletteEnterOutcome.Executed;
        }
        finally { if (version == _version) IsPending = false; }
    }

    /// <summary>Retry-control activation has no execution callback, including keyboard activation.</summary>
    public async Task<bool> RetryAsync(PaletteViewModel viewModel, Func<bool> canContinue)
    {
        if (IsPending || viewModel.IsExecuting || viewModel.SearchError == null || !canContinue()) return false;
        long version = ++_version;
        IsPending = true;
        var query = viewModel.SearchText;
        var invocation = viewModel.CurrentSnapshot?.InvocationId;
        try
        {
            var retry = viewModel.RetrySearchAsync();
            await retry;
            return version == _version && query == viewModel.SearchText &&
                invocation == viewModel.CurrentSnapshot?.InvocationId && ReferenceEquals(retry, viewModel.SearchTask) && canContinue();
        }
        finally { if (version == _version) IsPending = false; }
    }
}
