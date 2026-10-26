using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Core;

namespace ContextBar.SampleAdapters.Actions;

/// <summary>
/// A sample action that simulates reversing text and saving to a file.
/// </summary>
public class ReverseTextAction : IAction
{
    public ActionDescriptor Descriptor { get; } = new ActionDescriptor(
        Id: "sample.text.reverse",
        Title: ContextBar.SampleAdapters.Resources.Strings.Action_ReverseText_Title,
        Description: ContextBar.SampleAdapters.Resources.Strings.Action_ReverseText_Description,
        Accepts: new HashSet<ContentKind> { ContentKind.Text },
        SearchTerms: new[] { "reverse", "text", "flip" },
        RequiresPreview: true,
        ChangesFiles: true
    );

    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Content is TextSelection textSelection && !string.IsNullOrWhiteSpace(textSelection.Text))
        {
            return new ValueTask<ActionAvailability>(new ActionAvailability(true));
        }

        return new ValueTask<ActionAvailability>(new ActionAvailability(false, ContextBar.SampleAdapters.Resources.Strings.RequiresNonEmptyText));
    }

    public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Content is not TextSelection textSelection)
        {
            return new ValueTask<ActionPreview>(new ActionPreview(false, ContextBar.SampleAdapters.Resources.Strings.NoTextToReverse, Array.Empty<string>(), Array.Empty<string>()));
        }

        var reversedPreview = ReverseString(textSelection.Text);
        if (reversedPreview.Length > 20)
        {
            reversedPreview = reversedPreview.Substring(0, 20) + "...";
        }

        return new ValueTask<ActionPreview>(new ActionPreview(
            RequiresConfirmation: true,
            PreviewText: ContextBar.SampleAdapters.Resources.Strings.ReversePreview(reversedPreview),
            ExpectedOutputs: new[] { "C:\\Simulated\\Output\\Reversed.txt" },
            Warnings: Array.Empty<string>()
        ));
    }

    public async Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
             return new ActionReceipt(false, true, Array.Empty<string>(), Array.Empty<ActionFailure>(), false);
        }

        if (context.IsStale)
        {
            return new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, ContextBar.SampleAdapters.Resources.Strings.ContextStale) }, false);
        }

        if (context.Content is not TextSelection textSelection)
        {
            return new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, ContextBar.SampleAdapters.Resources.Strings.InvalidContext) }, false);
        }

        progress?.Report(new ActionProgress(0, null, ContextBar.SampleAdapters.Resources.Strings.StartingTextReversal));

        // Simulate some asynchronous work
        try
        {
            await Task.Delay(100, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new ActionReceipt(false, true, Array.Empty<string>(), Array.Empty<ActionFailure>(), false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
             return new ActionReceipt(false, true, Array.Empty<string>(), Array.Empty<ActionFailure>(), false);
        }

        var reversed = ReverseString(textSelection.Text);

        progress?.Report(new ActionProgress(100, null, ContextBar.SampleAdapters.Resources.Strings.TextReversedSuccessfully));

        return new ActionReceipt(
            IsSuccess: true,
            IsCanceled: false,
            OutputPaths: new[] { "C:\\Simulated\\Output\\Reversed.txt" },
            Failures: Array.Empty<ActionFailure>(),
            IsUndoable: false,
            Message: ContextBar.SampleAdapters.Resources.Strings.ReverseSuccessMessage
        );
    }

    private static string ReverseString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        char[] charArray = s.ToCharArray();
        Array.Reverse(charArray);
        return new string(charArray);
    }
}
