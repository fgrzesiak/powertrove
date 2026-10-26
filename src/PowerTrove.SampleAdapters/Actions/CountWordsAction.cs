using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Core;

namespace PowerTrove.SampleAdapters.Actions;

/// <summary>
/// A sample action that counts words in the selected text.
/// </summary>
public class CountWordsAction : IAction
{
    public ActionDescriptor Descriptor { get; } = new ActionDescriptor(
        Id: "sample.text.count-words",
        Title: PowerTrove.SampleAdapters.Resources.Strings.Action_CountWords_Title,
        Description: PowerTrove.SampleAdapters.Resources.Strings.Action_CountWords_Description,
        Accepts: new HashSet<ContentKind> { ContentKind.Text },
        SearchTerms: new[] { "word", "count", "text" },
        RequiresPreview: true,
        ChangesFiles: false
    );

    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Content is TextSelection textSelection && !string.IsNullOrWhiteSpace(textSelection.Text))
        {
            return new ValueTask<ActionAvailability>(new ActionAvailability(true));
        }

        return new ValueTask<ActionAvailability>(new ActionAvailability(false, PowerTrove.SampleAdapters.Resources.Strings.RequiresNonEmptyText));
    }

    public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Content is not TextSelection textSelection)
        {
            return new ValueTask<ActionPreview>(new ActionPreview(false, PowerTrove.SampleAdapters.Resources.Strings.NoTextToCount, Array.Empty<string>(), Array.Empty<string>()));
        }

        var wordCount = GetWordCount(textSelection.Text);
        return new ValueTask<ActionPreview>(new ActionPreview(
            RequiresConfirmation: false,
            PreviewText: PowerTrove.SampleAdapters.Resources.Strings.WordsCounted(wordCount),
            ExpectedOutputs: Array.Empty<string>(),
            Warnings: Array.Empty<string>()
        ));
    }

    public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new ActionReceipt(false, true, Array.Empty<string>(), Array.Empty<ActionFailure>(), false));
        }

        if (context.IsStale)
        {
            return Task.FromResult(new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, PowerTrove.SampleAdapters.Resources.Strings.ContextStale) }, false));
        }

        if (context.Content is not TextSelection textSelection)
        {
            return Task.FromResult(new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, PowerTrove.SampleAdapters.Resources.Strings.InvalidContext) }, false));
        }

        var wordCount = GetWordCount(textSelection.Text);

        progress?.Report(new ActionProgress(100, null, PowerTrove.SampleAdapters.Resources.Strings.CountCompleted));

        return Task.FromResult(new ActionReceipt(
            IsSuccess: true,
            IsCanceled: false,
            OutputPaths: Array.Empty<string>(),
            Failures: Array.Empty<ActionFailure>(),
            IsUndoable: false,
            Message: PowerTrove.SampleAdapters.Resources.Strings.SuccessfullyCountedWords(wordCount)
        ));
    }

    private static int GetWordCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
