using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions.Files;

namespace ContextBar.Pack.Files;

public sealed class CountFilesAction : IAction
{
    private readonly IDirectoryScanner _scanner;

    public ActionDescriptor Descriptor { get; } = new ActionDescriptor(
        Id: "contextbar.files.count",
        Title: SR.GetString("CountFilesAction_Title"),
        Description: SR.GetString("CountFilesAction_Description"),
        Accepts: new HashSet<ContentKind> { ContentKind.Files },
        SearchTerms: new[] { "count", "files", "directory", "folder", "items" },
        RequiresPreview: true,
        ChangesFiles: false,
        RequiredCapabilities: null,
        Selection: SelectionRequirements.Any("folder")
    );

    public CountFilesAction(IDirectoryScanner scanner)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    }

    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
    {
        if (context.Content is not FileSelection selection)
            return new ValueTask<ActionAvailability>(new ActionAvailability(false, SR.GetString("Error_NotAFileSelection")));

        // Only allow if we have at least one directory selected.
        if (!selection.Files.Any(f => f.IsDirectory))
            return new ValueTask<ActionAvailability>(new ActionAvailability(false, SR.GetString("CountFilesAction_Error_NoDirectories")));

        return new ValueTask<ActionAvailability>(new ActionAvailability(true));
    }

    public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        if (context.Content is not FileSelection selection)
            return new ValueTask<ActionPreview>(new ActionPreview(false, SR.GetString("Error_NotAFileSelection"), Array.Empty<string>(), Array.Empty<string>()));

        int totalCount = 0;
        bool anyLimitReached = false;

        foreach (var file in selection.Files.Where(f => f.IsDirectory && !string.IsNullOrEmpty(f.Path)))
        {
            var result = _scanner.EnumerateFiles(file.Path!, maxDepth: 5, maxItems: 10000, followSymlinks: false);
            totalCount += result.TotalItemsFound;
            anyLimitReached |= result.LimitReached;
        }

        string limitNote = anyLimitReached ? SR.GetString("CountFilesAction_Preview_LimitReachedNote") : "";
        string previewText = SR.Format("FoundItems" + (totalCount == 1 ? "_One" : "_Other"), totalCount, limitNote);

        return new ValueTask<ActionPreview>(new ActionPreview(
            RequiresConfirmation: false,
            PreviewText: previewText,
            ExpectedOutputs: Array.Empty<string>(),
            Warnings: anyLimitReached ? new[] { SR.GetString("CountFilesAction_Preview_ScanLimitWarning") } : Array.Empty<string>()
        ));
    }

    public async Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
    {
        if (context.IsStale)
        {
            return new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, SR.GetString("Error_ContextExpired")) }, false);
        }

        if (context.Content is not FileSelection selection)
            return new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, SR.GetString("Error_NotAFileSelection")) }, false);

        // Read-only: report the count as the result message. Scanning runs off the UI thread.
        var directories = selection.Files.Where(f => f.IsDirectory && !string.IsNullOrEmpty(f.Path)).ToList();
        var (total, limited) = await Task.Run(() =>
        {
            int count = 0;
            bool limit = false;
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = _scanner.EnumerateFiles(directory.Path!, maxDepth: 5, maxItems: 10000, followSymlinks: false);
                count += result.TotalItemsFound;
                limit |= result.LimitReached;
            }
            return (count, limit);
        }, cancellationToken).ConfigureAwait(false);

        string message = SR.Format("CountFilesResult", SR.Count("CountFiles", total), SR.Count("CountFolders", directories.Count), (limited ? SR.GetString("CountFilesAction_Execute_LimitNote") : string.Empty));
        return new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false, message);
    }
}
