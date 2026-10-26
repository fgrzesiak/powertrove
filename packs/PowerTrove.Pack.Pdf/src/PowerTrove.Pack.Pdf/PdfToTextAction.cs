using System.Text;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf;

public sealed record PdfToTextOptions();

public sealed class PdfToTextAction : PerFileAction<PdfToTextOptions>
{
    public PdfToTextAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "contextbar.pdf.to-text", SR.GetString("PdfToTextAction_Title"),
            SR.GetString("PdfToTextAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "text", "txt", "extract" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: Array.Empty<ActionParameterDefinition>(),
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }

    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override PdfToTextOptions ReadOptions(ActionParameterValues values) => new();

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, PdfToTextOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(file.Name), ".txt");
    }

    protected override string DescribeOperation(int inputCount, PdfToTextOptions options) =>
        string.Format(SR.GetString("PdfToTextAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override Task ProcessAsync(FileWorkItem item, PdfToTextOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        return output.WriteFileAsync(item.Directory, item.BaseName, ".txt", async (stream, ct) =>
        {
            var document = await PdfSupport.LoadAsync(item.File.Path!, ct);
            await Task.Run(() =>
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                bool anyText = false;
                for (int i = 0; i < document.PageCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (i > 0) writer.Write("\f"); // Form feed separating pages
                    string text = PdfText.Extract(document, i);
                    if (text.Length > 0)
                    {
                        anyText = true;
                        writer.Write(text);
                    }
                }
                writer.Flush();
                if (!anyText)
                {
                    throw new FileProcessingException(SR.GetString("PdfToTextAction_Warning_NoText"));
                }
            }, ct).ConfigureAwait(false);
        }, cancellationToken);
    }
}
