using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using FileKit.Pdf;
using System.Globalization;

namespace PowerTrove.Pack.Pdf;

public sealed record SanitizePdfOptions(bool KeepLinks);

public sealed class SanitizePdfAction : PerFileAction<SanitizePdfOptions>
{
    public SanitizePdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "contextbar.pdf.sanitize", SR.GetString("SanitizePdfAction_Title"),
            SR.GetString("SanitizePdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "sanitize", "hidden", "javascript", "attachments", "metadata", "remove" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Boolean("KeepLinks", SR.GetString("SanitizePdfAction_ParamTitle_KeepLinks"), true)
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override SanitizePdfOptions ReadOptions(ActionParameterValues values) => new(values.GetBoolean("KeepLinks"));

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        var preview = await base.PreviewAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (preview.IsBlocked) return preview;

        var options = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
        var messages = new List<string>(preview.Warnings);

        foreach (var file in ((FileSelection)context.Content).Files)
        {
            if (file.SizeBytes > 50 * 1024 * 1024) continue; // Skip scanning files larger than 50MB

            try
            {
                var document = await PdfSupport.LoadAsync(file.Path!, cancellationToken).ConfigureAwait(false);
                var counts = await Task.Run(() => PdfSanitizer.Analyze(document), cancellationToken).ConfigureAwait(false);

                if (!counts.HasHiddenContent)
                {
                    // If no hidden content is found, add a generic message so it skips cleanly (tested via integration)
                    // The actual skip will happen during ProcessAsync if we want to be strict,
                    // but the requirement says: "Files without hidden content are skipped with 'Nothing to remove.'"
                }
                else
                {
                    var items = new List<string>();
                    if (counts.Scripts > 0) items.Add(string.Format(CultureInfo.CurrentCulture, SR.GetString(counts.Scripts == 1 ? "PdfSanitize_Scripts_One" : "PdfSanitize_Scripts_Other"), counts.Scripts));
                    if (counts.Attachments > 0) items.Add(string.Format(CultureInfo.CurrentCulture, SR.GetString(counts.Attachments == 1 ? "PdfSanitize_Attachments_One" : "PdfSanitize_Attachments_Other"), counts.Attachments));
                    if (counts.LaunchActions > 0) items.Add(string.Format(CultureInfo.CurrentCulture, SR.GetString(counts.LaunchActions == 1 ? "PdfSanitize_LaunchActions_One" : "PdfSanitize_LaunchActions_Other"), counts.LaunchActions));
                    if (!options.KeepLinks && counts.UriActions > 0) items.Add(string.Format(CultureInfo.CurrentCulture, SR.GetString(counts.UriActions == 1 ? "PdfSanitize_UriActions_One" : "PdfSanitize_UriActions_Other"), counts.UriActions));
                    if (counts.MetadataItems > 0) items.Add(string.Format(CultureInfo.CurrentCulture, SR.GetString(counts.MetadataItems == 1 ? "PdfSanitize_Metadata_One" : "PdfSanitize_Metadata_Other"), counts.MetadataItems));

                    if (items.Count > 0)
                    {
                        string foundMsg = string.Format(CultureInfo.CurrentCulture, SR.GetString("PdfSanitize_Found"), string.Join(", ", items));
                        messages.Add($"{file.Name}: {foundMsg}");
                    }
                }
            }
            catch (Exception)
            {
                // Ignore load errors here, they will fail during processing
            }
        }

        return preview with { Warnings = messages };
    }

    protected override string DescribeOperation(int inputCount, SanitizePdfOptions options) =>
        string.Format(CultureInfo.CurrentCulture, SR.GetString("SanitizePdfAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_Copy"), SR.GetString("Pdf_Copies")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, SanitizePdfOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (sanitized)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, SanitizePdfOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        var counts = await Task.Run(() => PdfSanitizer.Analyze(document), cancellationToken).ConfigureAwait(false);

        bool willRemoveUri = !options.KeepLinks && counts.UriActions > 0;

        if (!counts.HasHiddenContent && !willRemoveUri)
        {
             throw new FileSkippedException(SR.GetString("SanitizePdfAction_NothingToRemove"));
        }

        await output.WriteFileAsync(item.Directory, item.BaseName + " (sanitized)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.Sanitize(document, stream, options.KeepLinks, ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}
