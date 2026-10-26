using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf;

public sealed record NUpPdfOptions(int PerSheet, PdfPageSize SheetSize);

public sealed class NUpPdfAction : PerFileAction<NUpPdfOptions>
{
    public const string ActionId = "contextbar.pdf.n-up";

    public NUpPdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            ActionId, SR.GetString("NUpPdfAction_Title"),
            SR.GetString("NUpPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "n-up", "print", "multiple", "pages", "sheet" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("PerSheet", SR.GetString("NUpPdfAction_ParamTitle_PerSheet"), "2", new[]
                {
                    new ActionParameterChoice("2", "2"),
                    new ActionParameterChoice("4", "4")
                }),
                ActionParameterDefinition.Choice("Sheet", SR.GetString("NUpPdfAction_ParamTitle_Sheet"), "a4", new[]
                {
                    new ActionParameterChoice("a4", "A4"),
                    new ActionParameterChoice("letter", "Letter")
                })
            },
            Selection: SelectionRequirements.Single("pdf"));
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override NUpPdfOptions ReadOptions(ActionParameterValues values)
    {
        int perSheet = values.GetChoice("PerSheet") == "4" ? 4 : 2;
        PdfPageSize size = values.GetChoice("Sheet") == "letter" ? PdfPageSize.Letter : PdfPageSize.A4;
        return new NUpPdfOptions(perSheet, size);
    }

    protected override string DescribeOperation(int inputCount, NUpPdfOptions options) =>
        string.Format(CultureInfo.CurrentCulture, SR.GetString("NUpPdfAction_Operation"), options.PerSheet);

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, NUpPdfOptions options) { yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + $" ({options.PerSheet}-up)", ".pdf"); }

    protected override async Task ProcessAsync(FileWorkItem item, NUpPdfOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var planned = PlanOutputs(item.File, options).First();
        await output.WriteFileAsync(item.Directory, planned.BaseName, planned.Extension,
            (stream, ct) => PdfSupport.RunAsync(() =>
            {
                var doc = PdfDocument.Open(item.Path!);
                PdfTools.CreateNUp(doc, stream, options.PerSheet, options.SheetSize, cancellationToken: ct);
            }, ct),
            cancellationToken).ConfigureAwait(false);
    }
}
