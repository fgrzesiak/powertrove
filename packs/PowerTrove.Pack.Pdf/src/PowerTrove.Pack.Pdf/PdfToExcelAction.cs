using FileKit.Tabular;
using FileKit.Xlsx;
using Cell = FileKit.Xlsx.Cell;
using EmptyCell = FileKit.Xlsx.EmptyCell;
using TextCell = FileKit.Xlsx.TextCell;

namespace PowerTrove.Pack.Pdf;

public sealed class PdfToExcelAction(FileActionServices services, IPositionedDocumentReader? reader) : PerFileAction<bool>(services)
{
    public override ActionDescriptor Descriptor { get; } = new("powertrove.pdf.to-xlsx", SR.GetString("PdfExcelTitle"), SR.GetString("PdfExcelDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["pdf", "excel", "xlsx", "table", "extract"], true, true,
        Parameters: [ActionParameterDefinition.Boolean("InferUnruled", SR.GetString("PdfExcelInfer"), true)], Selection: SelectionRequirements.Any("pdf"));
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override string? UnavailableReason => reader is null ? SR.GetString("PdfExcelUnavailable") : null;
    protected override bool ReadOptions(ActionParameterValues values) => values.GetBoolean("InferUnruled");
    protected override string DescribeOperation(int count, bool options) => SR.GetString("PdfExcelDescription");
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> files, bool options) => [SR.GetString("PdfExcelLimits")];
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, bool options) { yield return new(Path.GetFileNameWithoutExtension(file.Name), ".xlsx"); }
    private async Task<IReadOnlyList<DetectedTable>> Tables(string path, bool infer, CancellationToken ct)
    {
        await using var source = Services.FileSystem.OpenRead(path); var pages = await reader!.ReadAsync(source, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var tables = new List<DetectedTable>(); long cells = 0;
            foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested(); tables.AddRange(LayoutTableDetector.Detect(page, infer, ct));
                if (tables.Count > 128) throw new TableLayoutException("PDF table count limit exceeded.");
            }
            foreach (var table in tables) foreach (var row in table.Rows) if ((cells += row.Count) > 1000000) throw new TableLayoutException("PDF table cell limit exceeded.");
            if (tables.Count == 0) throw new TableLayoutException("No supported table structure was detected.");
            return (IReadOnlyList<DetectedTable>)tables.AsReadOnly();
        }, ct).ConfigureAwait(false);
    }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false); if (preview.IsBlocked) return preview;
        try
        {
            int count = 0; bool infer = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
            foreach (var file in ((FileSelection)context.Content).Files.Where(Input.Accepts)) count += (await Tables(file.Path!, infer, ct).ConfigureAwait(false)).Count;
            return preview with { PreviewText = string.Format(SR.GetString("PdfExcelPreview"), count) };
        }
        catch (TableLayoutException) { return preview with { IsBlocked = true, PreviewText = SR.GetString("PdfExcelInvalid") }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return preview with { IsBlocked = true, PreviewText = SR.GetString("PdfExcelReadError") }; }
    }
    protected override async Task ProcessAsync(FileWorkItem item, bool infer, FileOutputWriter output, CancellationToken ct)
    {
        try
        {
            var tables = await Tables(item.Path, infer, ct).ConfigureAwait(false); var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var numbers = new Dictionary<int, int>();
            var sheets = tables.Select(table =>
            {
                int number = numbers.GetValueOrDefault(table.Page) + 1; numbers[table.Page] = number;
                string name = SheetNames.Unique(string.Format(SR.GetString("PdfExcelSheet"), table.Page, number), used);
                return new Worksheet(name, table.Rows.Select(row => (IReadOnlyList<Cell>)row.Select(value => value.Length == 0 ? (Cell)new EmptyCell() : new TextCell(value)).ToArray()).ToArray());
            }).ToArray();
            await output.WriteFileAsync(item.Directory, item.BaseName, ".xlsx", (stream, token) => XlsxWriter.WriteAsync(stream, new(sheets), token), ct).ConfigureAwait(false);
        }
        catch (TableLayoutException ex) { throw new FileProcessingException(SR.GetString("PdfExcelInvalid"), ex); }
        catch (XlsxException ex) { throw new FileProcessingException(SR.GetString("PdfExcelWriteError"), ex); }
    }
}
