using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions;
using ContextBar.Abstractions.Files;
using FileKit.Pdf;
using System.Globalization;

namespace ContextBar.Pack.Pdf;

public sealed class RepairPdfAction : PerFileAction<NoOptions>
{
    public RepairPdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "contextbar.pdf.repair", SR.GetString("RepairPdfAction_Title"),
            SR.GetString("RepairPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "repair", "fix", "corrupt", "recover" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: null,
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override NoOptions ReadOptions(ActionParameterValues values) => new();

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        var preview = await base.PreviewAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (preview.IsBlocked) return preview;

        var messages = new List<string>(preview.Warnings);
        foreach (var file in ((FileSelection)context.Content).Files)
        {
            if (file.SizeBytes > 50 * 1024 * 1024) continue; // Skip scanning files larger than 50MB

            try
            {
                var document = await PdfSupport.LoadAsync(file.Path!, cancellationToken).ConfigureAwait(false);
                if (document.WasRepaired)
                {
                    messages.Add($"{file.Name}{SR.GetString("PdfInfoAction_Repaired")}");
                }
            }
            catch (Exception)
            {
                // Let it fail in process async
            }
        }

        return preview with { Warnings = messages };
    }

    protected override string DescribeOperation(int inputCount, NoOptions options) =>
        string.Format(CultureInfo.CurrentCulture, SR.GetString("RepairPdfAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, NoOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (repaired)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, NoOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);

        if (!document.WasRepaired)
        {
             throw new FileSkippedException(SR.GetString("RepairPdfAction_NoProblems"));
        }

        await output.WriteFileAsync(item.Directory, item.BaseName + " (repaired)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.Repair(document, stream, new PdfWriteOptions { Compact = true }, ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}
