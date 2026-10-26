using T = FileKit.Tabular;
using X = FileKit.Xlsx;

namespace PowerTrove.Pack.Tables;

public sealed class CombineWorkbookAction(FileActionServices services) : CombineFilesAction<bool>(services)
{
    public override ActionDescriptor Descriptor => new("powertrove.table.combine-workbook", SR.GetString("CombineWorkbook"), SR.GetString("CombineWorkbookDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["excel", "xlsx", "workbook", "combine", "csv"], true, true,
        Selection: new SelectionRequirements(new HashSet<string> { "spreadsheet" }, MinItems: 2, MaxItems: 50, AllowOtherItems: false,
            FamilyLimits: new Dictionary<string, FamilyLimit>(StringComparer.OrdinalIgnoreCase) { ["spreadsheet"] = new(2, 50) }));
    protected override bool RequiresEntireSelection => true;
    protected override int MaximumInputs => 50;
    protected override FileInputFilter Input => new(SR.GetString("SpreadsheetFiles"), [".xlsx", ".csv", ".tsv"], null);
    protected override bool ReadOptions(ActionParameterValues values) => true;
    protected override string DescribeOperation(int count, bool options) => SR.Format("CombineWorkbookOperation", count);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, bool options) => [SR.GetString("CachedFormulas")];
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, bool options) => new(Path.GetFileNameWithoutExtension(inputs[0].Name) + " (combined)", ".xlsx");
    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, bool options, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct)
    {
        var sheets = new List<X.Worksheet>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long cells = 0;
        try
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await using var input = Services.FileSystem.OpenRead(item.Path);
                IEnumerable<X.Worksheet> incoming;
                if (item.File.Extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    var book = await X.XlsxReader.ReadAsync(input, cancellationToken: ct).ConfigureAwait(false);
                    incoming = book.Sheets.Select(s => s with { Name = book.Sheets.Count == 1 ? item.BaseName : item.BaseName + " - " + s.Name });
                }
                else
                {
                    var table = await T.CsvReader.ReadAsync(input, firstRowAsHeader: false, cancellationToken: ct).ConfigureAwait(false);
                    incoming = [new X.Worksheet(item.BaseName, table.Rows.Select(r => (IReadOnlyList<X.Cell>)r.Select(c => c is T.TextCell t ? (X.Cell)new X.TextCell(t.Value) : new X.EmptyCell()).ToArray()).ToArray(), true)];
                }
                foreach (var sheet in incoming)
                {
                    cells += sheet.Rows.Sum(r => (long)r.Count);
                    if (sheets.Count >= 256 || cells > 4000000) throw new X.XlsxException("Workbook limit exceeded.");
                    sheets.Add(sheet with { Name = X.SheetNames.Unique(sheet.Name, names) });
                }
                progress?.Report((double)(item.Index + 1) / items.Count);
            }
            await output.WriteFileAsync(items[0].Directory, items[0].BaseName + " (combined)", ".xlsx", (s, token) => X.XlsxWriter.WriteAsync(s, new(sheets), token), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is X.XlsxException or T.TabularException)
        { throw new FileProcessingException(SR.GetString("InvalidWorkbook"), ex); }
    }
}
