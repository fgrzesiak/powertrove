using System.Text.Json;
using T = FileKit.Tabular;
using X = FileKit.Xlsx;

namespace PowerTrove.Pack.Data;

public sealed record JsonExcelOptions(bool Flatten, string Columns);

public sealed class JsonToExcelAction(FileActionServices services) : PerFileAction<JsonExcelOptions>(services)
{
    public override ActionDescriptor Descriptor => new("powertrove.data.json-to-xlsx", SR.Get("json-to-xlsx"), SR.Get("JsonExcelDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["json", "excel", "xlsx", "table", "convert"], true, true,
        Parameters: [ActionParameterDefinition.Boolean("Flatten", SR.Get("JsonExcelFlatten"), false),
            ActionParameterDefinition.MultiSelect("Columns", SR.Get("JsonExcelColumns"), SR.Get("JsonExcelColumnsHelp"))],
        Selection: SelectionRequirements.Any("text"));
    protected override FileInputFilter Input => new(SR.Get("Files"), [".json"], null);
    protected override JsonExcelOptions ReadOptions(ActionParameterValues values) => new(values.GetBoolean("Flatten"), values.GetText("Columns"));
    protected override string? ValidateOptions(JsonExcelOptions options)
    {
        try { _ = Columns(options.Columns); return null; }
        catch (Exception ex) when (ex is JsonException or T.TabularException) { return SR.Get("JsonExcelInvalidColumns"); }
    }
    protected override string DescribeOperation(int count, JsonExcelOptions options) => string.Format(SR.Get("Operation"), count);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> files, JsonExcelOptions options) => [SR.Get("JsonExcelTypes")];
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, JsonExcelOptions options)
    { yield return new(Path.GetFileNameWithoutExtension(file.Name), ".xlsx"); }
    private static string[]? Columns(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 2 });
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() is < 1 or > 16384 ||
            doc.RootElement.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(v.GetString())))
            throw new T.TabularException("Invalid columns.");
        var result = doc.RootElement.EnumerateArray().Select(v => v.GetString()!).ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length) throw new T.TabularException("Duplicate columns.");
        return result;
    }
    private async Task<T.Table> ReadAsync(Stream input, JsonExcelOptions options, CancellationToken ct)
    {
        using var document = await DataSupport.ReadJson(input, ct).ConfigureAwait(false);
        return await Task.Run(() => T.JsonObjectTable.Project(document.RootElement, options.Flatten, Columns(options.Columns), ct), ct).ConfigureAwait(false);
    }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        // Discover fields independently of a stale/empty selection so the dialog can repair it.
        var options = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
        var discoveryParameters = new DictionaryParameters(new Dictionary<string, string> { ["Flatten"] = options.Flatten ? "true" : "false", ["Columns"] = "" });
        var preview = await base.PreviewAsync(context, discoveryParameters, ct).ConfigureAwait(false);
        var choiceSet = new ActionParameterChoiceSet("Columns", options.Flatten ? "flattened" : "nested", [], SR.Get("JsonExcelColumnsHelp"));
        if (preview.IsBlocked) return preview with { ParameterChoices = [choiceSet] };
        try
        {
            long rows = 0; int columns = 0; List<string>? common = null; int fileCount = 0;
            string[]? selected = null;
            bool invalidSelection = false;
            try { selected = Columns(options.Columns); }
            catch (Exception ex) when (ex is JsonException or T.TabularException) { invalidSelection = true; }
            foreach (var file in ((FileSelection)context.Content).Files.Where(Input.Accepts))
            {
                ct.ThrowIfCancellationRequested();
                await using var stream = Services.FileSystem.OpenRead(file.Path!);
                using var document = await DataSupport.ReadJson(stream, ct).ConfigureAwait(false);
                var fields = await Task.Run(() => T.JsonObjectTable.DiscoverColumns(document.RootElement, options.Flatten, ct), ct).ConfigureAwait(false);
                var names = fields.ToHashSet(StringComparer.Ordinal);
                if (common is null) common = fields.ToList(); else common.RemoveAll(c => !names.Contains(c));
                fileCount++;
                if (!invalidSelection && (selected is null || selected.All(names.Contains)))
                {
                    var table = await Task.Run(() => T.JsonObjectTable.Project(document.RootElement, options.Flatten, selected, ct), ct).ConfigureAwait(false);
                    rows += table.Rows.Count; columns = Math.Max(columns, table.Headers!.Count);
                }
                else invalidSelection = true;
            }
            bool supported = common is { Count: <= ActionParameterChoiceSet.MaximumChoices } && common.All(c =>
                !string.IsNullOrWhiteSpace(c) && c.Length <= ActionParameterChoiceSet.MaximumChoiceLength && !c.Contains('\0'));
            choiceSet = choiceSet with
            {
                Choices = supported ? common!.Select(c => new ActionParameterChoice(c, c)).ToArray() : [],
                Description = !supported ? SR.Get("JsonExcelColumnsTooLarge") : common!.Count == 0 ? SR.Get("JsonExcelNoCommonColumns") :
                    fileCount > 1 ? SR.Get("JsonExcelCommonColumns") : SR.Get("JsonExcelColumnsHelp")
            };
            return preview with { PreviewText = invalidSelection ? SR.Get("JsonExcelInvalidColumns") : string.Format(SR.Get("Dimensions"), rows, columns),
                IsBlocked = invalidSelection, ParameterChoices = [choiceSet] };
        }
        catch (Exception ex) when (ex is T.TabularException or FileProcessingException or JsonException)
        { return preview with { IsBlocked = true, PreviewText = SR.Get("JsonExcelInvalid"), ParameterChoices = [choiceSet] }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return preview with { IsBlocked = true, PreviewText = SR.Get("ReadError"), ParameterChoices = [choiceSet] }; }
    }
    protected override async Task ProcessAsync(FileWorkItem item, JsonExcelOptions options, FileOutputWriter output, CancellationToken ct)
    {
        try
        {
            await using var input = Services.FileSystem.OpenRead(item.Path);
            var table = await ReadAsync(input, options, ct).ConfigureAwait(false);
            var rows = new List<IReadOnlyList<X.Cell>> { table.Headers!.Select(h => (X.Cell)new X.TextCell(h)).ToArray() };
            rows.AddRange(table.Rows.Select(r => (IReadOnlyList<X.Cell>)r.Select(ExcelAction.ToXlsx).ToArray()));
            var workbook = new X.Workbook([new X.Worksheet("Data", rows, HasHeader: true)]);
            await output.WriteFileAsync(item.Directory, item.BaseName, ".xlsx", (stream, token) => X.XlsxWriter.WriteAsync(stream, workbook, token), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is T.TabularException or X.XlsxException or JsonException)
        { throw new FileProcessingException(SR.Get("JsonExcelInvalid"), ex); }
    }
}
