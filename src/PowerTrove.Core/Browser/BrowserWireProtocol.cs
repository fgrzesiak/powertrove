using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerTrove.Core.Browser;

/// <summary>
/// Browser companion v1 envelope. This is a pure contract; the authenticated pipe,
/// window matching and actions are implemented by subsequent integration work.
/// </summary>
public static class BrowserWireProtocol
{
    public const int CurrentVersion = 1;
    public const int MinimumVersion = 1;
    // Native host -> Chromium must stay below Chromium's 1 MiB input-message limit.
    public const int MaximumMessageBytes = 960 * 1024;
    public const int MaximumChunkBytes = 512 * 1024;
    public const int MaximumTextBytes = 2 * 1024 * 1024;
    public const int MaximumImageBytes = 24 * 1024 * 1024;
    public const int MaximumMhtmlBytes = 64 * 1024 * 1024;
    public const int MaximumTableRows = 5000;
    public const int MaximumTableColumns = 200;
    public const int MaximumLinks = 200;
    public const int MaximumImages = 100;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> AllowedOperations =
        ["getContext", "captureVisible", "captureFullPage", "captureArea", "saveMhtml",
         "extractArticle", "extractPage", "extractTables", "listLinks", "listImages",
         "createPdf", "cancelCapture"];
    private static readonly HashSet<string> AllowedErrors =
        ["restricted", "permission", "navigated", "timeout", "tooLarge", "canceled", "failed"];
    private static readonly HashSet<string> AllowedKinds = ["image", "mhtml", "text", "pdf"];
    private static readonly Regex CapabilityName = new(@"^[a-z][a-zA-Z0-9.-]{0,49}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public abstract record Frame(string Type)
    {
        // The page title, selection, payload and URL must not appear in diagnostics.
        public override string ToString() => "BrowserWireFrame { Type = " + Type + ", Redacted }";
    }
    public sealed record Hello(string Browser, string ExtensionVersion, int MinProtocol,
        int MaxProtocol, IReadOnlyList<string> Capabilities) : Frame("hello")
    { public override string ToString() => base.ToString(); }
    public sealed record Ready(int MinProtocol, int MaxProtocol, IReadOnlyList<string> Capabilities)
        : Frame("ready")
    { public override string ToString() => base.ToString(); }
    public sealed record TabHandle(Guid SessionId, int WindowId, int TabId, string DocumentId)
    { public override string ToString() => "BrowserTabHandle { Redacted }"; }
    public sealed record Request(Guid Id, string Action, int DeadlineMs, TabHandle? Tab = null,
        string? WindowTitle = null) : Frame("request")
    { public override string ToString() => base.ToString(); }
    public sealed record Response(Guid Id, bool Ok, JsonElement? Result = null, string? Error = null)
        : Frame("response")
    { public override string ToString() => base.ToString(); }
    public sealed record Cancel(Guid Id) : Frame("cancel")
    { public override string ToString() => base.ToString(); }
    public sealed record Chunk(Guid Id, int Seq, bool Last, string Kind, string Base64)
        : Frame("chunk")
    { public override string ToString() => base.ToString(); }

    public static int? Negotiate(int peerMin, int peerMax)
    {
        if (peerMin < 1 || peerMax < peerMin || peerMax > 999) return null;
        var chosen = Math.Min(CurrentVersion, peerMax);
        return chosen >= Math.Max(MinimumVersion, peerMin) ? chosen : null;
    }

    public static Frame? Decode(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is 0 or > MaximumMessageBytes) return null;
        try
        {
            _ = StrictUtf8.GetCharCount(utf8);
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 10 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !NoDuplicates(root) ||
                !ReadString(root, "type", 16, out string? type)) return null;
            Frame? frame = type switch
            {
                "hello" when Exact(root, "type", "browser", "extensionVersion", "minProtocol", "maxProtocol", "capabilities") &&
                    ReadString(root, "browser", 8, out var browser) && browser is "chrome" or "edge" &&
                    ReadString(root, "extensionVersion", 32, out var version) && Version.TryParse(version, out _) &&
                    ReadInt(root, "minProtocol", 1, 999, out var hMin) &&
                    ReadInt(root, "maxProtocol", hMin, 999, out var hMax) &&
                    ReadCapabilities(root, out var capabilities)
                    => new Hello(browser!, version!, hMin, hMax, capabilities!),
                "ready" when Exact(root, "type", "minProtocol", "maxProtocol", "capabilities") &&
                    ReadInt(root, "minProtocol", 1, 999, out var rMin) &&
                    ReadInt(root, "maxProtocol", rMin, 999, out var rMax) &&
                    ReadCapabilities(root, out var readyCapabilities)
                    => new Ready(rMin, rMax, readyCapabilities!),
                "request" when (Exact(root, "type", "id", "action", "deadlineMs") ||
                    Exact(root, "type", "id", "action", "deadlineMs", "tab") ||
                    Exact(root, "type", "id", "action", "deadlineMs", "windowTitle") ||
                    Exact(root, "type", "id", "action", "deadlineMs", "tab", "windowTitle")) &&
                    ReadId(root, out var requestId) &&
                    ReadString(root, "action", 32, out var action) && AllowedOperations.Contains(action!) &&
                    ReadInt(root, "deadlineMs", 1, 30000, out var deadline) &&
                    ReadTab(root, out var tab) &&
                    ReadOptionalString(root, "windowTitle", 300, out var title)
                    => new Request(requestId, action!, deadline, tab, title),
                "response" when (Exact(root, "type", "id", "ok", "result") ||
                    Exact(root, "type", "id", "ok", "error")) &&
                    ReadId(root, out var responseId) &&
                    ReadBool(root, "ok", out var ok) &&
                    ReadResponse(root, ok, out var result, out var error)
                    => new Response(responseId, ok, result, error),
                "cancel" when Exact(root, "type", "id") &&
                    ReadId(root, out var cancelId) => new Cancel(cancelId),
                "chunk" when Exact(root, "type", "id", "seq", "last", "kind", "base64") &&
                    ReadId(root, out var chunkId) &&
                    ReadInt(root, "seq", 0, 1024, out var seq) &&
                    ReadBool(root, "last", out var last) &&
                    ReadString(root, "kind", 16, out var kind) && AllowedKinds.Contains(kind!) &&
                    ReadString(root, "base64", 710000, out var base64) &&
                    CheckBase64(base64!)
                    => new Chunk(chunkId, seq, last, kind!, base64!),
                _ => null
            };
            return frame;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException
            or ArgumentException or FormatException or OverflowException)
        {
            return null;
        }
    }

    public static byte[] Encode(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", frame.Type);
            switch (frame)
            {
                case Hello x:
                    writer.WriteString("browser", x.Browser);
                    writer.WriteString("extensionVersion", x.ExtensionVersion);
                    writer.WriteNumber("minProtocol", x.MinProtocol);
                    writer.WriteNumber("maxProtocol", x.MaxProtocol);
                    WriteCapabilities(writer, x.Capabilities);
                    break;
                case Ready x:
                    writer.WriteNumber("minProtocol", x.MinProtocol);
                    writer.WriteNumber("maxProtocol", x.MaxProtocol);
                    WriteCapabilities(writer, x.Capabilities);
                    break;
                case Request x:
                    writer.WriteString("id", x.Id);
                    writer.WriteString("action", x.Action);
                    writer.WriteNumber("deadlineMs", x.DeadlineMs);
                    if (x.Tab is { } tab)
                    {
                        writer.WriteStartObject("tab");
                        writer.WriteString("sessionId", tab.SessionId);
                        writer.WriteNumber("windowId", tab.WindowId);
                        writer.WriteNumber("tabId", tab.TabId);
                        writer.WriteString("documentId", tab.DocumentId);
                        writer.WriteEndObject();
                    }
                    if (x.WindowTitle is not null) writer.WriteString("windowTitle", x.WindowTitle);
                    break;
                case Response x:
                    writer.WriteString("id", x.Id);
                    writer.WriteBoolean("ok", x.Ok);
                    if (x.Ok)
                    {
                        writer.WritePropertyName("result");
                        if (x.Result is { } result) result.WriteTo(writer);
                        else writer.WriteStartObject(); // empty structured success result
                        if (x.Result is null) writer.WriteEndObject();
                    }
                    else writer.WriteString("error", x.Error);
                    break;
                case Cancel x:
                    writer.WriteString("id", x.Id);
                    break;
                case Chunk x:
                    writer.WriteString("id", x.Id);
                    writer.WriteNumber("seq", x.Seq);
                    writer.WriteBoolean("last", x.Last);
                    writer.WriteString("kind", x.Kind);
                    writer.WriteString("base64", x.Base64);
                    break;
                default: throw new InvalidDataException("Unsupported browser frame.");
            }
            writer.WriteEndObject();
        }
        byte[] data = stream.ToArray();
        if (Decode(data) is null) throw new InvalidDataException("Invalid browser frame.");
        return data;
    }

    private static void WriteCapabilities(Utf8JsonWriter writer, IReadOnlyList<string> capabilities)
    {
        writer.WriteStartArray("capabilities");
        foreach (var capability in capabilities) writer.WriteStringValue(capability);
        writer.WriteEndArray();
    }
    private static bool Exact(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var supplied = value.EnumerateObject().Select(p => p.Name).ToArray();
        return supplied.Length == keys.Length && supplied.ToHashSet(StringComparer.Ordinal).SetEquals(keys);
    }
    private static bool NoDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().All(NoDuplicates);
        if (value.ValueKind != JsonValueKind.Object) return true;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(p => names.Add(p.Name) && NoDuplicates(p.Value));
    }
    private static bool ReadString(JsonElement obj, string key, int max, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return value is { Length: > 0 } && value.Length <= max && !value.Contains('\0') && Unicode(value);
    }
    private static bool ReadOptionalString(JsonElement obj, string key, int max, out string? value)
    {
        value = null;
        return !obj.TryGetProperty(key, out _) || ReadString(obj, key, max, out value);
    }
    private static bool Unicode(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
            }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
    private static bool ReadInt(JsonElement obj, string key, int min, int max, out int value)
    {
        value = 0;
        return obj.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.Number &&
            x.TryGetInt32(out value) && value >= min && value <= max;
    }
    private static bool ReadBool(JsonElement obj, string key, out bool value)
    {
        value = false;
        if (!obj.TryGetProperty(key, out var x) || x.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = x.GetBoolean();
        return true;
    }
    private static bool ReadId(JsonElement root, out Guid id)
    {
        id = default;
        return ReadString(root, "id", 36, out var value) && value!.Length == 36 &&
            Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;
    }
    private static bool ReadCapabilities(JsonElement root, out IReadOnlyList<string>? capabilities)
    {
        capabilities = null;
        if (!root.TryGetProperty("capabilities", out var array) ||
            array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 16) return false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                item.GetString() is not { } value || !CapabilityName.IsMatch(value) || !found.Add(value)) return false;
        }
        capabilities = found.OrderBy(c => c, StringComparer.Ordinal).ToArray();
        return true;
    }
    private static bool ReadTab(JsonElement root, out TabHandle? tab)
    {
        tab = null;
        if (!root.TryGetProperty("tab", out var element)) return true;
        if (!Exact(element, "sessionId", "windowId", "tabId", "documentId") ||
            !ReadString(element, "sessionId", 36, out var session) ||
            session!.Length != 36 || !Guid.TryParseExact(session, "D", out var sessionId) || sessionId == Guid.Empty ||
            !ReadInt(element, "windowId", 1, int.MaxValue, out var windowId) ||
            !ReadInt(element, "tabId", 0, int.MaxValue, out var tabId) ||
            !ReadString(element, "documentId", 128, out var doc)) return false;
        tab = new TabHandle(sessionId, windowId, tabId, doc!);
        return true;
    }
    private static bool ReadResponse(JsonElement root, bool ok, out JsonElement? result, out string? error)
    {
        result = null; error = null;
        if (ok)
        {
            if (!root.TryGetProperty("result", out var item) || item.ValueKind != JsonValueKind.Object)
                return false;
            result = item.Clone();
            return true;
        }
        if (!ReadString(root, "error", 16, out error)) return false;
        return AllowedErrors.Contains(error!);
    }
    private static bool CheckBase64(string value)
    {
        if (value.Length > 4 * ((MaximumChunkBytes + 2) / 3) || value.Length % 4 != 0) return false;
        // Reject noncanonical or malformed base64 before the caller allocates a large buffer.
        byte[] bytes = Convert.FromBase64String(value);
        return bytes.Length is > 0 and <= MaximumChunkBytes &&
            Convert.ToBase64String(bytes).Equals(value, StringComparison.Ordinal);
    }

    public static int MaximumTotalBytes(string kind) => kind switch
    {
        "image" or "pdf" => MaximumImageBytes,
        "mhtml" => MaximumMhtmlBytes,
        "text" => MaximumTextBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
