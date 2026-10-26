using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions;
using ContextBar.Abstractions.Files;
using FileKit.Pdf;

namespace ContextBar.Pack.Pdf;

public sealed record BookletPdfOptions(PdfPageSize SheetSize);

public sealed class BookletPdfAction : PerFileAction<BookletPdfOptions>
{
    public const string ActionId = "contextbar.pdf.booklet";

    public BookletPdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            ActionId, SR.GetString("BookletPdfAction_Title"),
            SR.GetString("BookletPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "booklet", "print", "saddle", "stitch" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("Sheet", SR.GetString("BookletPdfAction_ParamTitle_Sheet"), "a4", new[]
                {
                    new ActionParameterChoice("a4", "A4"),
                    new ActionParameterChoice("letter", "Letter")
                })
            },
            Selection: SelectionRequirements.Single("pdf"));
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override BookletPdfOptions ReadOptions(ActionParameterValues values)
    {
        PdfPageSize size = values.GetChoice("Sheet") == "letter" ? PdfPageSize.Letter : PdfPageSize.A4;
        return new BookletPdfOptions(size);
    }

    protected override string DescribeOperation(int inputCount, BookletPdfOptions options) =>
        SR.GetString("BookletPdfAction_Operation");

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, BookletPdfOptions options) { yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (booklet)", ".pdf"); }

    protected override async Task ProcessAsync(FileWorkItem item, BookletPdfOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var planned = PlanOutputs(item.File, options).First();
        await output.WriteFileAsync(item.Directory, planned.BaseName, planned.Extension,
            (stream, ct) => PdfSupport.RunAsync(() =>
            {
                var doc = PdfDocument.Open(item.Path!);
                PdfTools.CreateBooklet(doc, stream, options.SheetSize, cancellationToken: ct);
            }, ct),
            cancellationToken).ConfigureAwait(false);
    }
}
