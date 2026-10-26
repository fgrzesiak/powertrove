using System.Text;
using T = FileKit.Tabular;
using X = FileKit.Xlsx;

namespace PowerTrove.Pack.Data;

public sealed class ExcelAction : PerFileAction<string>
{
    private readonly string _mode;
    public ExcelAction(FileActionServices services, string mode) : base(services) => _mode = mode;
    public override ActionDescriptor Descriptor => new("powertrove.data." + _mode, SR.Get(_mode), SR.Get("ExcelDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["excel", "xlsx", "csv", "json", "convert"], true, true,
        Parameters: _mode == "csv-to-xlsx" ? [] : [DataSupport.Choice("Sheet", "first", "first", "all")],
        Selection: SelectionRequirements.Any("spreadsheet"));
    protected override FileInputFilter Input => new(SR.Get("Files"), _mode == "csv-to-xlsx" ? [".csv", ".tsv"] : [".xlsx"], null);
    protected override string ReadOptions(ActionParameterValues v) => _mode == "csv-to-xlsx" ? "first" : v.GetChoice("Sheet");
    protected override string DescribeOperation(int count, string options) => string.Format(SR.Get("Operation"), count);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, string options) => _mode == "csv-to-xlsx" ? [] : [SR.Get("CachedFormulas")];
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, string options)
    {
        yield return new(Path.GetFileNameWithoutExtension(file.Name), _mode == "csv-to-xlsx" ? ".xlsx" : _mode == "xlsx-to-json" ? ".json" : ".csv");
    }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false);
        if (preview.IsBlocked || _mode == "csv-to-xlsx") return preview;
        try
        {
            string option = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
            var planned = new List<string>(); var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in ((FileSelection)context.Content).Files.Where(Input.Accepts))
            {
                await using var input = Services.FileSystem.OpenRead(file.Path!);
                var book = await X.XlsxReader.ReadAsync(input, cancellationToken: ct).ConfigureAwait(false);
                foreach (var sheet in Chosen(book, option))
                {
                    string stem = Stem(file.Name, sheet.Name, option);
                    string path = Services.Planner.PlanFile(Path.GetDirectoryName(file.Path!)!, stem, Extension, reserved).TargetPath;
                    reserved.Add(path); planned.Add(path);
                }
            }
            return preview with { ExpectedOutputs = planned };
        }
        catch (X.XlsxException) { return preview with { IsBlocked = true, PreviewText = SR.Get("InvalidWorkbook") }; }
        catch (IOException) { return preview with { IsBlocked = true, PreviewText = SR.Get("ReadError") }; }
        catch (UnauthorizedAccessException) { return preview with { IsBlocked = true, PreviewText = SR.Get("ReadError") }; }
    }
    private string Extension => _mode == "xlsx-to-json" ? ".json" : ".csv";
    private static IEnumerable<X.Worksheet> Chosen(X.Workbook book, string option) => option == "all" ? book.Sheets : book.Sheets.Take(1);
    private static string Stem(string name, string sheet, string option) => Path.GetFileNameWithoutExtension(name) +
        (option == "all" ? " - " + SafeName(sheet) : "");
    private static string SafeName(string name)
    {
        string safe = new(name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray());
        safe = safe.TrimEnd(' ', '.'); return string.IsNullOrEmpty(safe) ? "_" : safe;
    }
    protected override async Task ProcessAsync(FileWorkItem item, string option, FileOutputWriter output, CancellationToken ct)
    {
        try
        {
            await using var input = Services.FileSystem.OpenRead(item.Path);
            if (_mode == "csv-to-xlsx")
            {
                var table = await T.CsvReader.ReadAsync(input, firstRowAsHeader: false, cancellationToken: ct).ConfigureAwait(false);
                var sheet = new X.Worksheet(X.SheetNames.Unique(item.BaseName, new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                    table.Rows.Select(r => (IReadOnlyList<X.Cell>)r.Select(ToXlsx).ToArray()).ToArray(), HasHeader: true);
                await output.WriteFileAsync(item.Directory, item.BaseName, ".xlsx", (s, token) => X.XlsxWriter.WriteAsync(s, new([sheet]), token), ct).ConfigureAwait(false);
                return;
            }
            var workbook = await X.XlsxReader.ReadAsync(input, cancellationToken: ct).ConfigureAwait(false);
            foreach (var sheet in Chosen(workbook, option))
            {
                ct.ThrowIfCancellationRequested();
                // Preserve every row. JSON arrays avoid guessing whether a workbook has headers.
                var table = new T.Table(null, sheet.Rows.Select(r => (IReadOnlyList<T.Cell>)r.Select(ToTabular).ToArray()).ToArray());
                await output.WriteFileAsync(item.Directory, Stem(item.File.Name, sheet.Name, option), Extension, async (stream, token) =>
                {
                    if (_mode == "xlsx-to-json") T.JsonTableWriter.Write(stream, table, cancellationToken: token);
                    else { await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true); T.CsvWriter.Write(writer, table, cancellationToken: token); }
                }, ct, allowEmpty: true).ConfigureAwait(false);
            }
        }
        catch (X.XlsxException ex) { throw new FileProcessingException(SR.Get("InvalidWorkbook"), ex); }
        catch (T.TabularException ex) { throw DataSupport.TabularError(ex); }
    }
    private static X.Cell ToXlsx(T.Cell c) => c switch
    {
        T.TextCell t => new X.TextCell(t.Value), T.NumberCell n => new X.NumberCell(n.Value), T.BooleanCell b => new X.BooleanCell(b.Value),
        T.DateCell d => new X.DateCell(d.Value), T.ErrorCell e => new X.ErrorCell(e.Code), _ => new X.EmptyCell()
    };
    private static T.Cell ToTabular(X.Cell c) => c switch
    {
        X.TextCell t => new T.TextCell(t.Value), X.NumberCell n => new T.NumberCell(n.Value), X.BooleanCell b => new T.BooleanCell(b.Value),
        X.DateCell d => new T.DateCell(d.Value), X.ErrorCell e => new T.ErrorCell(e.Code), _ => new T.EmptyCell()
    };
}
