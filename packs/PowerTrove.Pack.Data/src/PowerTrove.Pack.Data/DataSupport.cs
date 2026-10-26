using System.Text;
using System.Text.Json;
using FileKit.Tabular;

namespace PowerTrove.Pack.Data;

internal static class DataSupport
{
    public static readonly string[] TextExtensions = [".txt", ".csv", ".tsv", ".json", ".md", ".log", ".xml", ".yaml", ".yml", ".sql", ".ini", ".cfg", ".html", ".css", ".js", ".cs"];
    public static ActionParameterDefinition Choice(string name, string value, params string[] options) =>
        ActionParameterDefinition.Choice(name, SR.Get(name), value, options.Select(o => new ActionParameterChoice(o, SR.Get(o))).ToArray());
    public static Task WriteText(FileOutputWriter output, string directory, string stem, string extension, string text, CancellationToken ct, Encoding? encoding = null) =>
        output.WriteFileAsync(directory, stem, extension, async (stream, token) =>
        {
            await using var writer = new StreamWriter(stream, encoding ?? new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(text.AsMemory(), token).ConfigureAwait(false);
        }, ct, allowEmpty: true);
    public static async Task<JsonDocument> ReadJson(Stream stream, CancellationToken ct)
    {
        var text = await TextFileReader.ReadAsync(stream, ct).ConfigureAwait(false);
        try
        {
            var document = await Task.Run(() => JsonDocument.Parse(text.Text, new JsonDocumentOptions { MaxDepth = 256 }), ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) { document.Dispose(); ct.ThrowIfCancellationRequested(); }
            return document;
        }
        catch (JsonException ex) { throw new FileProcessingException(string.Format(SR.Get("InvalidJson"), (ex.LineNumber ?? 0) + 1, (ex.BytePositionInLine ?? 0) + 1)); }
    }
    public static Table JsonTable(JsonElement root, CancellationToken ct = default)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new FileProcessingException(SR.Get("JsonArray"));
        var headers = new List<string>(); var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in root.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (row.ValueKind != JsonValueKind.Object) throw new FileProcessingException(SR.Get("JsonArray"));
            var rowKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in row.EnumerateObject())
            {
                if (!rowKeys.Add(p.Name)) throw new FileProcessingException(SR.Get("JsonDuplicate"));
                if (keys.Add(p.Name)) headers.Add(p.Name);
                if (headers.Count > 16384) throw new FileProcessingException(SR.Get("TooLarge"));
            }
        }
        if (root.GetArrayLength() > 1_000_000 || root.GetArrayLength() * (long)headers.Count > 4_000_000) throw new FileProcessingException(SR.Get("TooLarge"));
        return new(headers, root.EnumerateArray().Select(row => (IReadOnlyList<Cell>)headers.Select(h =>
            !row.TryGetProperty(h, out var value) || value.ValueKind == JsonValueKind.Null ? (Cell)new FileKit.Tabular.EmptyCell()
            : new FileKit.Tabular.TextCell(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText())).ToArray()).ToArray());
    }
    public static FileProcessingException TabularError(TabularException ex) => new(SR.Get("InvalidTable"), ex);
}
