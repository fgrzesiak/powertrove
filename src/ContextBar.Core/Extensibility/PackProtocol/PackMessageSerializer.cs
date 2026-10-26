using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ContextBar.Core.Extensibility.PackProtocol;

/// <summary>Pure UTF-8 JSON Lines codec. Null means an unknown, well-formed message was ignored.</summary>
public static class PackMessageSerializer
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <returns>One compact JSON object plus LF. The size limit excludes the line terminator.</returns>
    public static byte[] SerializeLine(PackMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        PackMessageValidation.Validate(message);
        try
        {
            byte[] json = message switch
            {
                HelloMessage m => Write(m, PackJsonContext.Default.HelloMessage),
                ReadyMessage m => Write(m, PackJsonContext.Default.ReadyMessage),
                PreviewMessage m => Write(m, PackJsonContext.Default.PreviewMessage),
                ExecuteMessage m => Write(m, PackJsonContext.Default.ExecuteMessage),
                PreviewResultMessage m => Write(m, PackJsonContext.Default.PreviewResultMessage),
                ProgressMessage m => Write(m, PackJsonContext.Default.ProgressMessage),
                ResultMessage m => Write(m, PackJsonContext.Default.ResultMessage),
                CancelMessage m => Write(m, PackJsonContext.Default.CancelMessage),
                ErrorMessage m => Write(m, PackJsonContext.Default.ErrorMessage),
                HeartbeatMessage m => Write(m, PackJsonContext.Default.HeartbeatMessage),
                ShutdownMessage m => Write(m, PackJsonContext.Default.ShutdownMessage),
                _ => throw new PackProtocolException(PackProtocolError.InvalidMessage)
            };
            if (json.Length > PackProtocolLimits.MaximumLineBytes) throw new PackProtocolException(PackProtocolError.LineTooLarge);
            Array.Resize(ref json, json.Length + 1);
            json[^1] = (byte)'\n';
            return json;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new PackProtocolException(PackProtocolError.InvalidMessage);
        }
    }

    /// <summary>Accepts one complete line, with an optional LF or CRLF terminator. Never multiple lines.</summary>
    public static PackMessage? DeserializeLine(ReadOnlySpan<byte> line)
    {
        if (!line.IsEmpty && line[^1] == (byte)'\n') line = line[..^1];
        if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
        if (line.Length > PackProtocolLimits.MaximumLineBytes) throw new PackProtocolException(PackProtocolError.LineTooLarge);
        if (line.IsEmpty || line.IndexOf((byte)'\n') >= 0 || line.IndexOf((byte)'\r') >= 0)
            throw new PackProtocolException(PackProtocolError.MalformedJson);
        JsonDocument document;
        try
        {
            _ = StrictUtf8.GetCharCount(line);
            document = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            throw new PackProtocolException(PackProtocolError.MalformedJson);
        }
        using (document)
        {
            try
            {
                var root = document.RootElement;
                CheckDuplicates(root);
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String || type.GetString() is not { Length: > 0 and <= 64 } name)
                    throw new PackProtocolException(PackProtocolError.InvalidMessage);
                // Require the non-optional SDK receipt fields too (their SDK constructors have defaults).
                if (name == "result") RequireReceiptFields(root);
                PackMessage? message = name switch
                {
                    "hello" => Read(line, PackJsonContext.Default.HelloMessage),
                    "ready" => Read(line, PackJsonContext.Default.ReadyMessage),
                    "preview" => Read(line, PackJsonContext.Default.PreviewMessage),
                    "execute" => Read(line, PackJsonContext.Default.ExecuteMessage),
                    "previewResult" => Read(line, PackJsonContext.Default.PreviewResultMessage),
                    "progress" => Read(line, PackJsonContext.Default.ProgressMessage),
                    "result" => Read(line, PackJsonContext.Default.ResultMessage),
                    "cancel" => Read(line, PackJsonContext.Default.CancelMessage),
                    "error" => Read(line, PackJsonContext.Default.ErrorMessage),
                    "heartbeat" => Read(line, PackJsonContext.Default.HeartbeatMessage),
                    "shutdown" => Read(line, PackJsonContext.Default.ShutdownMessage),
                    _ => null
                };
                if (message is not null) PackMessageValidation.Validate(message);
                return message;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                throw new PackProtocolException(PackProtocolError.InvalidMessage);
            }
        }
    }

    private static byte[] Write<T>(T message, JsonTypeInfo<T> info) => JsonSerializer.SerializeToUtf8Bytes(message, info);
    private static T Read<T>(ReadOnlySpan<byte> json, JsonTypeInfo<T> info) where T : PackMessage =>
        JsonSerializer.Deserialize(json, info) ?? throw new PackProtocolException(PackProtocolError.InvalidMessage);

    private static void RequireReceiptFields(JsonElement root)
    {
        if (!root.TryGetProperty("receipt", out var receipt) || receipt.ValueKind != JsonValueKind.Object)
            throw new PackProtocolException(PackProtocolError.InvalidMessage);
        foreach (string name in new[] { "isSuccess", "isCanceled", "outputPaths", "failures", "isUndoable" })
            if (!receipt.TryGetProperty(name, out _)) throw new PackProtocolException(PackProtocolError.InvalidMessage);
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new PackProtocolException(PackProtocolError.InvalidMessage);
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }
}
