using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Core.Tests.Fakes;

public class FakeAction : IAction
{
    public ActionDescriptor Descriptor { get; init; } = new(
        Id: "FakeAction",
        Title: "Fake Action",
        Description: "A fake action for testing",
        Accepts: new HashSet<ContentKind> { ContentKind.Files, ContentKind.Table, ContentKind.Text },
        SearchTerms: new List<string> { "fake", "test" },
        RequiresPreview: false,
        ChangesFiles: false
    );

    // Configuration
    public ActionAvailability MatchResult { get; set; } = new(true);
    public ActionPreview PreviewResult { get; set; } = new(
        RequiresConfirmation: false,
        PreviewText: "Fake Preview",
        ExpectedOutputs: Array.Empty<string>(),
        Warnings: Array.Empty<string>()
    );
    public ActionReceipt ExecuteResult { get; set; } = new(
        IsSuccess: true,
        IsCanceled: false,
        OutputPaths: Array.Empty<string>(),
        Failures: Array.Empty<ActionFailure>(),
        IsUndoable: false
    );

    // Simulation of delays
    public TimeSpan ExecuteDelay { get; set; } = TimeSpan.Zero;

    // Track executions for verification
    public int ExecuteCallCount { get; private set; }

    public ValueTask<ActionAvailability> MatchAsync(
        ContextSnapshot context,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ActionAvailability>(cancellationToken);
        }
        return new ValueTask<ActionAvailability>(MatchResult);
    }

    public ValueTask<ActionPreview> PreviewAsync(
        ContextSnapshot context,
        ActionParameters parameters,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ActionPreview>(cancellationToken);
        }
        return new ValueTask<ActionPreview>(PreviewResult);
    }

    public async Task<ActionReceipt> ExecuteAsync(
        ContextSnapshot context,
        ActionParameters parameters,
        IProgress<ActionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ExecuteCallCount++;

        if (ExecuteDelay > TimeSpan.Zero)
        {
            // Simulate work and progress reports during delay
            var steps = 10;
            var stepDelay = ExecuteDelay / steps;

            for (int i = 0; i < steps; i++)
            {
                await Task.Delay(stepDelay, cancellationToken);
                progress?.Report(new ActionProgress(
                    Percentage: (i + 1) * 10.0,
                    CurrentItem: $"Fake item {i + 1}",
                    StatusMessage: "Fake progress"
                ));
            }
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ActionProgress(100.0, StatusMessage: "Complete"));
        }

        return ExecuteResult;
    }
}
