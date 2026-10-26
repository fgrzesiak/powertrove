using System.Globalization;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf;

/// <summary>
/// Read-only: shows page count, PDF version and first page size. Never shows
/// document titles, authors or text, which may be sensitive.
/// </summary>
public sealed class PdfInfoAction : IAction
{
    private const int MaxFiles = 20;
    private readonly IFileTypeVerifier _verifier;

    public PdfInfoAction(IFileTypeVerifier verifier)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    public ActionDescriptor Descriptor { get; } = new(
        "contextbar.pdf.info", SR.GetString("PdfInfoAction_Title"),
        SR.GetString("PdfInfoAction_Description"),
        new HashSet<ContentKind> { ContentKind.Files },
        new[] { "pdf", "info", "pages", "count", "details", "properties" },
        RequiresPreview: false, ChangesFiles: false,
        Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });

    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Content is not FileSelection selection)
            return new(new ActionAvailability(false, SR.GetString("PdfInfoAction_Error_NotFileSelection")));
        int count = selection.Files.Count(PdfSupport.PdfInput.Accepts);
        if (count == 0) return new(new ActionAvailability(false, SR.GetString("PdfInfoAction_Error_NoPdfs")));
        if (count > MaxFiles) return new(new ActionAvailability(false, string.Format(SR.GetString("PdfInfoAction_Error_MaxFiles"), MaxFiles)));
        return new(new ActionAvailability(true));
    }

    public async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var summary = await SummarizeAsync(context, cancellationToken).ConfigureAwait(false);
        return new ActionPreview(false, summary.Text, Array.Empty<string>(), summary.Warnings, IsBlocked: summary.Blocked);
    }

    public async Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var summary = await SummarizeAsync(context, cancellationToken).ConfigureAwait(false);
            return summary.Blocked
                ? new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, summary.Text) }, false, summary.Text)
                : new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false, summary.Text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ActionReceipt(false, true, Array.Empty<string>(), Array.Empty<ActionFailure>(), false, SR.GetString("PdfInfoAction_Canceled"));
        }
    }

    private async Task<(string Text, IReadOnlyList<string> Warnings, bool Blocked)> SummarizeAsync(ContextSnapshot context, CancellationToken cancellationToken)
    {
        if (context.IsStale) return (SR.GetString("PdfInfoAction_Error_Stale"), Array.Empty<string>(), true);
        if (context.Content is not FileSelection selection) return (SR.GetString("PdfInfoAction_Error_NotFileSelection"), Array.Empty<string>(), true);

        var files = selection.Files.Where(PdfSupport.PdfInput.Accepts).Take(MaxFiles).ToList();
        if (files.Count == 0) return (SR.GetString("PdfInfoAction_Error_NoPdfs"), Array.Empty<string>(), true);

        var lines = new List<string>();
        var warnings = new List<string>();
        int totalPages = 0;
        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string label = files.Count == 1 ? SR.GetString("Pdf_File") : string.Format(SR.GetString("PdfInfoAction_PdfLabel"), i + 1);
            var check = await _verifier.VerifyAsync(files[i].Path!, PdfSupport.PdfInput, cancellationToken).ConfigureAwait(false);
            if (!check.IsAccepted)
            {
                warnings.Add($"{label}: {check.Reason}");
                continue;
            }

            try
            {
                var document = await PdfSupport.LoadAsync(files[i].Path!, cancellationToken).ConfigureAwait(false);
                var info = PdfTools.GetInfo(document);
                totalPages += info.PageCount;
                lines.Add(string.Format(SR.GetString("PdfInfoAction_Details"), label, info.PageCount, info.Version, info.FirstPageWidthPoints * 25.4 / 72, info.FirstPageHeightPoints * 25.4 / 72, (info.WasRepaired ? SR.GetString("PdfInfoAction_Repaired") : string.Empty)));
            }
            catch (FileProcessingException ex)
            {
                warnings.Add($"{label}: {ex.Message}");
            }
        }

        if (lines.Count == 0) return (SR.GetString("PdfInfoAction_Error_NoReadablePdfs"), warnings, true);
        if (files.Count > 1) lines.Add(string.Format(SR.GetString("PdfInfoAction_TotalPages"), totalPages));
        return (string.Join(Environment.NewLine, lines), warnings, false);
    }
}
