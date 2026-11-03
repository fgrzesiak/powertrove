using System.Buffers.Binary;
using System.Text.Json;

namespace PowerTrove.Core.Activation;

/// <summary>A versioned, length-prefixed local protocol. Bounds apply before allocation or parsing.</summary>
public static class ActivationProtocol
{
    public const int MaxMessageBytes = 65536;
    public const int MaxFiles = 100;
    public const string BrowserConnectScheme = "powertrove";
    public const string BrowserConnectArgument = "--browser-connect";
    public const int MaxBrowserTextCharacters = 24576;
    public const int MaxBrowserTitleCharacters = 300;
    public const int MaxPathCharacters = 2048;
    public const string OrganizePagesActionId = "powertrove.pdf.organize";

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

    public static bool IsBrowserPairing(BrowserPairingMessage? message) =>
        message is { Browser: "chrome" or "edge", ExtensionId.Length: 32 } &&
        message.ExtensionId.All(c => c is >= 'a' and <= 'p');

    public static BrowserPairingMessage? ParseBrowserConnectUri(string? input)
    {
        if (string.IsNullOrEmpty(input) || input.Length > 300 ||
            !Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            uri.Scheme != BrowserConnectScheme || uri.Host != "connect" ||
            uri.UserInfo.Length != 0 || uri.Port != -1 || uri.AbsolutePath != "/" ||
            uri.Fragment.Length != 0) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = part.Split('=', 2);
            if (fields.Length != 2 || !values.TryAdd(fields[0], fields[1])) return null;
        }
        if (values.Count != 2 || !values.TryGetValue("browser", out var browser) ||
            !values.TryGetValue("id", out var id)) return null;
        var pairing = new BrowserPairingMessage(browser, id);
        return IsBrowserPairing(pairing) ? pairing : null;
    }

    public static bool IsBrowserSelection(BrowserSelectionMessage? message)
    {
        if (message is null || message.Text is null || message.Title is null || message.Url is null ||
            message.Text.Length is < 1 or > MaxBrowserTextCharacters ||
            message.Title.Length > MaxBrowserTitleCharacters || message.Url.Length > MaxPathCharacters ||
            message.Text.Contains('\0') || message.Title.Contains('\0') ||
            message.Url.Contains('\0') || !ValidUnicode(message.Text) || !ValidUnicode(message.Title)) return false;
        return Uri.TryCreate(message.Url, UriKind.Absolute, out var url) &&
            url.Scheme is ("https" or "http") && url.UserInfo.Length == 0;
    }

    public static bool IsBrowserTable(BrowserTableMessage? message)
    {
        if (message is null || message.Url is null || message.Title is null ||
            message.Rows is null || message.Title.Length > MaxBrowserTitleCharacters ||
            message.Url.Length > MaxPathCharacters || !ValidUnicode(message.Title) ||
            !Uri.TryCreate(message.Url, UriKind.Absolute, out var url) ||
            url.Scheme is not ("https" or "http") || url.UserInfo.Length != 0 ||
            message.Rows.Count is < 1 or > 100 || message.Rows.Any(row =>
                row is null || row.Count is < 1 or > 20 ||
                row.Any(cell => cell is null || cell.Length > 512 || cell.Contains('\0') || !ValidUnicode(cell))))
            return false;
        return message.Rows.Sum(row => row.Count) <= 2000;
    }

    private static bool ValidUnicode(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false;
            }
            else if (char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }

    public static bool IsActionId(string? id) => id is null || (id.Length is > 0 and <= 128
        && id.StartsWith("powertrove.", StringComparison.Ordinal)
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
                case BrowserPairingMessage pairing when IsBrowserPairing(pairing):
                    writer.WriteString("Type", "BrowserPairing");
                    writer.WriteString("Browser", pairing.Browser);
                    writer.WriteString("ExtensionId", pairing.ExtensionId);
                    break;
                case BrowserTableMessage table when IsBrowserTable(table):
                    writer.WriteString("Type", "BrowserTable");
                    writer.WriteString("Url", table.Url);
                    writer.WriteString("Title", table.Title);
                    writer.WriteStartArray("Rows");
                    foreach (var row in table.Rows)
                    {
                        writer.WriteStartArray();
                        foreach (var cell in row) writer.WriteStringValue(cell);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                    break;
                case BrowserSelectionMessage browser when IsBrowserSelection(browser):
                    writer.WriteString("Type", "BrowserSelection");
                    writer.WriteString("Text", browser.Text);
                    writer.WriteString("Url", browser.Url);
                    writer.WriteString("Title", browser.Title);
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
                if (!names.Add(property.Name) || property.Name is not ("Version" or "Type" or "FilePaths" or "ActionId" or "Text" or "Url" or "Title" or "Browser" or "ExtensionId" or "Rows")) return null;
            if (!root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out int value) || value != 1
                || !root.TryGetProperty("Type", out var type) || type.ValueKind != JsonValueKind.String) return null;
            if (type.GetString() == "ActivatePalette")
                return names.Count == 2 ? new ActivatePaletteMessage() : null;
            if (type.GetString() == "BrowserPairing")
            {
                if (names.Count != 4 || !root.TryGetProperty("Browser", out var name) || name.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("ExtensionId", out var extension) || extension.ValueKind != JsonValueKind.String) return null;
                var pairing = new BrowserPairingMessage(name.GetString()!, extension.GetString()!);
                return IsBrowserPairing(pairing) ? pairing : null;
            }
            if (type.GetString() == "BrowserTable")
            {
                if (names.Count != 5 || !root.TryGetProperty("Rows", out var rows) ||
                    rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 100 ||
                    !root.TryGetProperty("Url", out var tableUrl) || tableUrl.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("Title", out var tableTitle) || tableTitle.ValueKind != JsonValueKind.String) return null;
                var parsedRows = new List<IReadOnlyList<string>>();
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() is < 1 or > 20) return null;
                    var cells = new List<string>();
                    foreach (var cell in row.EnumerateArray())
                    {
                        if (cell.ValueKind != JsonValueKind.String) return null;
                        cells.Add(cell.GetString()!);
                    }
                    parsedRows.Add(cells.AsReadOnly());
                }
                var table = new BrowserTableMessage(tableUrl.GetString()!, tableTitle.GetString()!, parsedRows);
                return IsBrowserTable(table) ? table : null;
            }
            if (type.GetString() == "BrowserSelection")
            {
                if (names.Count != 5 || !root.TryGetProperty("Text", out var text) || text.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("Url", out var browserUrl) || browserUrl.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("Title", out var title) || title.ValueKind != JsonValueKind.String) return null;
                var browser = new BrowserSelectionMessage(text.GetString()!, browserUrl.GetString()!, title.GetString()!);
                return IsBrowserSelection(browser) ? browser : null;
            }
            if (names.Count is < 3 or > 4 || names.Any(n => n is not ("Version" or "Type" or "FilePaths" or "ActionId")) ||
                type.GetString() != "ExplicitSelection" || !root.TryGetProperty("FilePaths", out var paths)
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
        if (args is [BrowserConnectArgument, var url]) return ParseBrowserConnectUri(url);
        if (args.Count is < 2 or > MaxFiles + 1
            || args[0] is not ("--open-selection" or "--organize-pages")) return null;
        var message = new ExplicitSelectionMessage(Array.AsReadOnly(args.Skip(1).ToArray()),
            args[0] == "--organize-pages" ? OrganizePagesActionId : null);
        try { _ = Encode(message); return message; }
        catch (InvalidDataException) { return null; }
    }
}
