using System.Text;
using System.Text.Json;
using FileKit.Tabular;

namespace ContextBar.Pack.Data;

public sealed record DataOptions(string Mode, bool Bom, string Indent, string Dialect, string TableName);

public sealed class DataFileAction : PerFileAction<DataOptions>
{
    private readonly string _kind;
    public DataFileAction(FileActionServices services, string kind) : base(services)
    {
        _kind = kind;
        var parameters = kind switch
        {
            "to-utf8" => new[] { ActionParameterDefinition.Boolean("Bom", SR.Get("Bom"), false) },
            "line-endings" => new[] { DataSupport.Choice("Style", "windows", "windows", "unix") },
            "csv-to-json" => new[] { DataSupport.Choice("HeaderMode", "first-row", "first-row", "none") },
            "csv-to-sql" => new[] { DataSupport.Choice("Dialect", "postgresql", "postgresql", "mysql", "sqlite", "sqlserver"), ActionParameterDefinition.Text("TableName", SR.Get("TableName"), "ExportedData") },
            "json-format" => new[] { DataSupport.Choice("Indent", "2", "2", "4", "tabs") },
            _ => Array.Empty<ActionParameterDefinition>()
        };
        Descriptor = new("contextbar.data." + kind, SR.Get(kind), SR.Get("Description"), new HashSet<ContentKind> { ContentKind.Files },
            new[] { kind, "data", "text", "convert" }, true, true, Parameters: parameters,
            Selection: SelectionRequirements.Any(kind.StartsWith("csv", StringComparison.Ordinal) ? new[] { "spreadsheet", "text" } : kind.StartsWith("json", StringComparison.Ordinal) ? new[] { "text" } : new[] { "text", "spreadsheet", "other" }));
    }
    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => new(SR.Get("Files"), _kind.StartsWith("csv", StringComparison.Ordinal) ? new[] { ".csv", ".tsv" } : _kind.StartsWith("json", StringComparison.Ordinal) ? new[] { ".json" } : DataSupport.TextExtensions, null);
    protected override DataOptions ReadOptions(ActionParameterValues v) => new(
        _kind == "line-endings" ? v.GetChoice("Style") : _kind == "csv-to-json" ? v.GetChoice("HeaderMode") : "",
        _kind == "to-utf8" && v.GetBoolean("Bom"), _kind == "json-format" ? v.GetChoice("Indent") : "2",
        _kind == "csv-to-sql" ? v.GetChoice("Dialect") : "postgresql", _kind == "csv-to-sql" ? v.GetText("TableName") : "ExportedData");
    protected override string? ValidateOptions(DataOptions o) => _kind == "csv-to-sql" && string.IsNullOrWhiteSpace(o.TableName) ? SR.Get("InvalidTableName") : null;
    protected override string DescribeOperation(int n, DataOptions o) => string.Format(SR.Get("Operation"), n);
    private string Extension(SelectedFile f) => _kind switch { "csv-to-json" => ".json", "csv-to-markdown" => ".md", "csv-to-sql" => ".sql", "json-to-csv" => ".csv", _ => f.Extension };
    private string Suffix => _kind switch { "to-utf8" => "utf-8", "line-endings" => "line endings", "json-format" => "formatted", "json-minify" => "minified", _ => "converted" };
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile f, DataOptions o) { yield return new(Path.GetFileNameWithoutExtension(f.Name) + " (" + Suffix + ")", Extension(f)); }

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false);
        if (preview.IsBlocked || !_kind.StartsWith("csv", StringComparison.Ordinal)) return preview;
        try
        {
            int rows = 0, columns = 0;
            foreach (var f in ((FileSelection)context.Content).Files.Where(Input.Accepts))
            {
                await using var stream = Services.FileSystem.OpenRead(f.Path!);
                var options = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
                var table = await CsvReader.ReadAsync(stream, options.Mode != "none", cancellationToken: ct).ConfigureAwait(false);
                rows += table.Rows.Count; columns = Math.Max(columns, table.Headers?.Count ?? 0);
            }
            return preview with { PreviewText = string.Format(SR.Get("Dimensions"), rows, columns) };
        }
        catch (TabularException) { return preview with { IsBlocked = true, PreviewText = SR.Get("InvalidTable") }; }
        catch (IOException) { return preview with { IsBlocked = true, PreviewText = SR.Get("ReadError") }; }
        catch (UnauthorizedAccessException) { return preview with { IsBlocked = true, PreviewText = SR.Get("ReadError") }; }
    }

    protected override async Task ProcessAsync(FileWorkItem item, DataOptions o, FileOutputWriter output, CancellationToken ct)
    {
        await using var input = Services.FileSystem.OpenRead(item.Path);
        var planned = PlanOutputs(item.File, o).Single();
        if (_kind.StartsWith("csv", StringComparison.Ordinal))
        {
            try
            {
                var table = await CsvReader.ReadAsync(input, o.Mode != "none", cancellationToken: ct).ConfigureAwait(false);
                await output.WriteFileAsync(item.Directory, planned.BaseName, planned.Extension, async (stream, token) =>
                {
                    if (_kind == "csv-to-json") JsonTableWriter.Write(stream, table, cancellationToken: token);
                    else
                    {
                        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                        if (_kind == "csv-to-markdown") MarkdownTableWriter.Write(writer, table, token);
                        else SqlInsertWriter.Write(writer, table, o.TableName, o.Dialect, token);
                    }
                }, ct, allowEmpty: true).ConfigureAwait(false);
            }
            catch (TabularException ex) { throw DataSupport.TabularError(ex); }
            return;
        }
        if (_kind.StartsWith("json", StringComparison.Ordinal))
        {
            using var doc = await DataSupport.ReadJson(input, ct).ConfigureAwait(false);
            if (_kind == "json-to-csv")
            {
                var table = DataSupport.JsonTable(doc.RootElement, ct);
                await output.WriteFileAsync(item.Directory, planned.BaseName, planned.Extension, async (stream, token) =>
                { await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true); CsvWriter.Write(writer, table, cancellationToken: token); }, ct, allowEmpty: true).ConfigureAwait(false);
            }
            else
                await output.WriteFileAsync(item.Directory, planned.BaseName, planned.Extension, (stream, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = _kind == "json-format", IndentCharacter = o.Indent == "tabs" ? '\t' : ' ', IndentSize = o.Indent == "4" ? 4 : o.Indent == "tabs" ? 1 : 2, MaxDepth = 256 });
                    doc.RootElement.WriteTo(writer); return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
            return;
        }
        var text = await TextFileReader.ReadAsync(input, ct).ConfigureAwait(false);
        string result; Encoding encoding;
        if (_kind == "to-utf8")
        {
            if (text.Encoding.CodePage == Encoding.UTF8.CodePage) throw new FileSkippedException();
            result = text.Text; encoding = new UTF8Encoding(o.Bom);
        }
        else
        {
            result = text.Text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (o.Mode == "windows") result = result.Replace("\n", "\r\n");
            if (result == text.Text) throw new FileSkippedException();
            encoding = text.Encoding;
        }
        await DataSupport.WriteText(output, item.Directory, planned.BaseName, planned.Extension, result, ct, encoding).ConfigureAwait(false);
    }
}
