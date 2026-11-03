using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerTrove.Pack.Markdown.Conversion;

/// <summary>
/// Jupyter notebooks (.ipynb) to Markdown: Markdown cells as written (with their attached images), code cells as
/// fenced code in the notebook's language and, optionally, their saved outputs: text, Markdown, images and errors.
/// Nothing is executed.
/// </summary>
internal static partial class NotebookConverter
{
    private const int MaximumOutputLines = 200;

    public static async Task<ConvertedMarkdown> ConvertAsync(Stream source, bool outputs, CancellationToken ct)
    {
        string json;
        try { json = (await FileKit.Text.TextDecoding.ReadAsync(source, 64L * 1024 * 1024, ct).ConfigureAwait(false)).Text; }
        catch (FileKit.Text.TextLimitException) { throw new FileProcessingException(SR.Get("TooLarge")); }
        catch (DecoderFallbackException) { throw new FileProcessingException(SR.Get("InvalidText")); }
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw new DocumentFormatException(SR.Get("ToMarkdownInvalid")); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
                throw new DocumentFormatException(SR.Get("ToMarkdownInvalid"));
            string language = Language(root);
            var output = new MarkdownText();
            foreach (var cell in cells.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (cell.ValueKind != JsonValueKind.Object) continue;
                string type = cell.TryGetProperty("cell_type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : string.Empty;
                string text = Source(cell, "source");
                switch (type)
                {
                    case "markdown": output.Block(Attachments(text, cell, output)); break;
                    case "code":
                        if (text.Trim().Length > 0) output.Block(MarkdownText.Fence(text, language));
                        if (outputs && cell.TryGetProperty("outputs", out var results) && results.ValueKind == JsonValueKind.Array)
                            foreach (var result in results.EnumerateArray()) Output(result, output);
                        break;
                    case "raw": if (text.Trim().Length > 0) output.Block(MarkdownText.Fence(text)); break;
                }
            }
            if (output.IsEmpty) throw new DocumentFormatException(SR.Get("ToMarkdownNoContent"));
            return output.Build();
        }
    }

    private static string Language(JsonElement root)
    {
        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var (container, key) in new[] { ("language_info", "name"), ("kernelspec", "language") })
                if (metadata.TryGetProperty(container, out var info) && info.ValueKind == JsonValueKind.Object && info.TryGetProperty(key, out var name)
                    && name.ValueKind == JsonValueKind.String && LanguageName().IsMatch(name.GetString()!))
                    return name.GetString()!.ToLowerInvariant();
        }
        return string.Empty;
    }

    /// <summary>Notebook text is a string or a list of lines.</summary>
    private static string Source(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Array => string.Concat(value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())),
            _ => string.Empty
        };
    }

    /// <summary>Images pasted into a Markdown cell travel inside the notebook as "attachment:" references.</summary>
    private static string Attachments(string markdown, JsonElement cell, MarkdownText output)
    {
        if (!cell.TryGetProperty("attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Object) return markdown;
        return Attachment().Replace(markdown, match =>
        {
            string name = match.Groups[2].Value;
            if (attachments.TryGetProperty(Uri.UnescapeDataString(name), out var data) && data.ValueKind == JsonValueKind.Object)
                foreach (var item in data.EnumerateObject())
                    if (Decode(item.Value) is { } bytes && ImageKinds.Extension(bytes) is { } extension)
                        return output.Image(bytes, extension, match.Groups[1].Value);
            return match.Value;
        });
    }

    private static void Output(JsonElement result, MarkdownText output)
    {
        if (result.ValueKind != JsonValueKind.Object) return;
        string type = result.TryGetProperty("output_type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : string.Empty;
        if (type == "stream") { Text(Source(result, "text"), output); return; }
        if (type == "error")
        {
            string name = result.TryGetProperty("ename", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "Error";
            string value = result.TryGetProperty("evalue", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
            Text(name + ": " + value, output);
            return;
        }
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        foreach (string image in new[] { "image/png", "image/jpeg", "image/gif", "image/svg+xml" })
            if (data.TryGetProperty(image, out var value))
            {
                byte[]? bytes = image == "image/svg+xml" ? Encoding.UTF8.GetBytes(Source(data, image)) : Decode(value);
                if (bytes is not null && ImageKinds.Extension(bytes) is { } extension) { output.Block(output.Image(bytes, extension, null)); return; }
            }
        if (data.TryGetProperty("text/markdown", out _)) { output.Block(Source(data, "text/markdown")); return; }
        if (data.TryGetProperty("text/plain", out _)) Text(Source(data, "text/plain"), output);
    }

    private static void Text(string text, MarkdownText output)
    {
        var lines = Ansi().Replace(text, string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        if (lines.All(l => l.Trim().Length == 0)) return;
        string body = string.Join('\n', lines.Take(MaximumOutputLines)) + (lines.Length > MaximumOutputLines ? "\n…" : string.Empty);
        output.Block(MarkdownText.Fence(body, "text"));
    }

    private static byte[]? Decode(JsonElement value)
    {
        string text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Array => string.Concat(value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())),
            _ => string.Empty
        };
        try { return text.Length is > 0 and < 40_000_000 ? Convert.FromBase64String(Whitespace().Replace(text, string.Empty)) : null; }
        catch (FormatException) { return null; }
    }

    [GeneratedRegex(@"!\[([^\]\n]*)\]\(attachment:([^)\s]+)\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Attachment();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Ansi();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+#._-]{0,31}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LanguageName();
}
