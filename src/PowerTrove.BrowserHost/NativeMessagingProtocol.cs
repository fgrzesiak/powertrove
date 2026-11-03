using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PowerTrove.Core.Activation;

namespace PowerTrove.BrowserHost;

/// <summary>Chrome/Edge native messaging: one bounded little-endian length-prefixed JSON message per invocation.</summary>
public static class NativeMessagingProtocol
{
    public const int MaximumPayloadBytes = 96 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static BrowserSelectionMessage? Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > MaximumPayloadBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!keys.Add(property.Name) || property.Name is not ("type" or "text" or "url" or "title")) return null;
            if (keys.Count != 4 || root.GetProperty("type").GetString() != "openSelection") return null;
            var text = root.GetProperty("text");
            var url = root.GetProperty("url");
            var title = root.GetProperty("title");
            if (text.ValueKind != JsonValueKind.String || url.ValueKind != JsonValueKind.String ||
                title.ValueKind != JsonValueKind.String) return null;
            var selection = new BrowserSelectionMessage(text.GetString()!, url.GetString()!, title.GetString()!);
            // The desktop's own protocol validates the same payload, independently.
            return ActivationProtocol.IsBrowserSelection(selection) ? selection : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or DecoderFallbackException) { return null; }
    }

    public static ActivationMessage? DecodeActivation(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > MaximumPayloadBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions { MaxDepth = 5 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return null;
            if (type.GetString() == "openDownload")
            {
                var entries = root.EnumerateObject().ToArray();
                if (entries.Length != 2 || entries.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
                    !root.TryGetProperty("path", out var file) || file.ValueKind != JsonValueKind.String ||
                    !ActivationProtocol.IsLocalPath(file.GetString())) return null;
                var selected = new ExplicitSelectionMessage([file.GetString()!]);
                _ = ActivationProtocol.Encode(selected);
                return selected;
            }
            if (type.GetString() != "openTable") return Decode(payload);
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!fields.Add(property.Name) || property.Name is not ("type" or "url" or "title" or "rows"))
                    return null;
            if (fields.Count != 4 ||
                !root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array ||
                rows.GetArrayLength() is < 1 or > 100 ||
                root.GetProperty("url").ValueKind != JsonValueKind.String ||
                root.GetProperty("title").ValueKind != JsonValueKind.String) return null;
            var result = new List<IReadOnlyList<string>>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() is < 1 or > 20) return null;
                var cells = new List<string>();
                foreach (var value in row.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String) return null;
                    cells.Add(value.GetString()!);
                }
                result.Add(cells.AsReadOnly());
            }
            var table = new BrowserTableMessage(root.GetProperty("url").GetString()!,
                root.GetProperty("title").GetString()!, result);
            if (!ActivationProtocol.IsBrowserTable(table)) return null;
            _ = ActivationProtocol.Encode(table); // Enforce 64 KiB pipe limit before transport.
            return table;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or DecoderFallbackException or InvalidDataException)
        { return null; }
    }

    public static async Task<BrowserSelectionMessage?> ReadAsync(Stream input, CancellationToken token)
    {
        var header = new byte[4];
        await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaximumPayloadBytes) return null;
        var content = new byte[size];
        await input.ReadExactlyAsync(content, token).ConfigureAwait(false);
        return Decode(content);
    }

    public static async Task WriteResponseAsync(Stream output, bool ok, CancellationToken token)
    {
        byte[] payload = Utf8.GetBytes(ok ? "{\"ok\":true}" : "{\"ok\":false}");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await output.WriteAsync(header, token).ConfigureAwait(false);
        await output.WriteAsync(payload, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }
}
