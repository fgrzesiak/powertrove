using System.Buffers.Binary;
using System.Text.Json;

namespace ContextBar.Core.Activation;

/// <summary>A versioned, length-prefixed local protocol. Bounds apply before allocation or parsing.</summary>
public static class ActivationProtocol
{
    public const int MaxMessageBytes = 65536;
    public const int MaxFiles = 100;
    public const int MaxPathCharacters = 2048;
    public const string OrganizePagesActionId = "contextbar.pdf.organize";

    // Syntax only. The Windows adapter additionally checks existence, drive type and reparse points.
    public static bool IsLocalPath(string? path)
    {
        if (path is null || path.Length < 4 || path.Length > MaxPathCharacters
            || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
        if (path.AsSpan(3).ContainsAny('<', '>', ':') || path.IndexOfAny(['"', '|', '?', '*', '/']) >= 0
            || path.Any(char.IsControl)) return false;
        return path[3..].Split('\\').All(part => part.Length > 0 && part is not "." and not ".."
            && !part.EndsWith('.') && !part.EndsWith(' '));
    }

    public static bool IsActionId(string? id) => id is null || (id.Length is > 0 and <= 128
        && id.StartsWith("contextbar.", StringComparison.Ordinal)
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'));

    public static byte[] Encode(ActivationMessage message)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", 1);
            switch (message)
            {
                case ActivatePaletteMessage:
                    writer.WriteString("Type", "ActivatePalette");
                    break;
                case ExplicitSelectionMessage selection when selection.FilePaths is { Count: > 0 and <= MaxFiles }
                    && selection.FilePaths.All(IsLocalPath) && IsActionId(selection.ActionId):
                    writer.WriteString("Type", "ExplicitSelection");
                    writer.WriteStartArray("FilePaths");
                    foreach (var path in selection.FilePaths) writer.WriteStringValue(path);
                    writer.WriteEndArray();
                    if (selection.ActionId is not null) writer.WriteString("ActionId", selection.ActionId);
                    break;
                default: throw new InvalidDataException("Invalid activation message.");
            }
            writer.WriteEndObject();
        }
        if (buffer.Length > MaxMessageBytes) throw new InvalidDataException("Activation message exceeds the limit.");
        return buffer.ToArray();
    }

    public static ActivationMessage? Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxMessageBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("Version" or "Type" or "FilePaths" or "ActionId")) return null;
            if (!root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out int value) || value != 1
                || !root.TryGetProperty("Type", out var type) || type.ValueKind != JsonValueKind.String) return null;
            if (type.GetString() == "ActivatePalette")
                return names.Count == 2 ? new ActivatePaletteMessage() : null;
            if (type.GetString() != "ExplicitSelection" || !root.TryGetProperty("FilePaths", out var paths)
                || paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() is 0 or > MaxFiles) return null;
            var files = new List<string>();
            foreach (var path in paths.EnumerateArray())
            {
                if (path.ValueKind != JsonValueKind.String || !IsLocalPath(path.GetString())) return null;
                files.Add(path.GetString()!);
            }
            string? actionId = null;
            if (root.TryGetProperty("ActionId", out var action))
            {
                if (action.ValueKind != JsonValueKind.String || !IsActionId(actionId = action.GetString())) return null;
            }
            return new ExplicitSelectionMessage(Array.AsReadOnly(files.ToArray()), actionId);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    public static async Task WriteAsync(Stream stream, ActivationMessage message, CancellationToken token)
    {
        byte[] payload = Encode(message);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<ActivationMessage?> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes) return null;
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return Decode(payload);
    }

    /// <summary>Main's argument vector excludes the executable. Invalid explicit input is never generic activation.</summary>
    public static ActivationMessage? ParseArguments(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new ActivatePaletteMessage();
        if (args.Count is < 2 or > MaxFiles + 1
            || args[0] is not ("--open-selection" or "--organize-pages")) return null;
        var message = new ExplicitSelectionMessage(Array.AsReadOnly(args.Skip(1).ToArray()),
            args[0] == "--organize-pages" ? OrganizePagesActionId : null);
        try { _ = Encode(message); return message; }
        catch (InvalidDataException) { return null; }
    }
}
