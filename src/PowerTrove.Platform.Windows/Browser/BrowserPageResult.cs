using System.Text.Json;

namespace PowerTrove.Platform.Windows.Browser;

/// <summary>Bounded result of a live explicit getContext request. Never log or display as a record.</summary>
public sealed class BrowserPageResult
{
    public Guid SessionId { get; private init; }
    public string DocumentId { get; private init; } = "";
    public string Browser { get; private init; } = "";
    public string Title { get; private init; } = "";
    public string WindowTitle { get; private init; } = "";
    public string Url { get; private init; } = "";
    public string Selection { get; private init; } = "";
    public bool Focused { get; private init; }
    public bool Incognito { get; private init; }
    public int TabId { get; private init; }
    public int WindowId { get; private init; }
    public bool IsArticle { get; private init; }
    public int TableCount { get; private init; }
    public int LargestTableRows { get; private init; }
    public int LargestTableColumns { get; private init; }
    public int ImageCount { get; private init; }
    public int CodeBlockCount { get; private init; }
    public int WordCount { get; private init; }
    public string? Language { get; private init; }
    public override string ToString() => "BrowserPageResult { Redacted }";

    public static BrowserPageResult? Read(string browser, JsonElement value, Guid sessionId = default)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        try
        {
            string title = value.GetProperty("title").GetString() ?? "";
            string windowTitle = value.GetProperty("windowTitle").GetString() ?? "";
            string url = value.GetProperty("url").GetString() ?? "";
            string selection = value.GetProperty("selection").GetString() ?? "";
            int tabId = value.GetProperty("tabId").GetInt32();
            int windowId = value.GetProperty("windowId").GetInt32();
            bool focused = value.GetProperty("focused").GetBoolean();
            bool incognito = value.GetProperty("incognito").GetBoolean();
            string documentId = value.TryGetProperty("documentId", out var doc) && doc.ValueKind == JsonValueKind.String
                ? doc.GetString() ?? "" : "";
            if (documentId.Length > 128) return null;
            if (incognito || title.Length is < 1 or > 300 || windowTitle.Length > 400 ||
                url.Length is < 8 or > 2048 || selection.Length > 24576 ||
                tabId < 0 || windowId < 1 || selection.Contains('\0') ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0) return null;
            JsonElement signals = value.TryGetProperty("signals", out var extra) &&
                extra.ValueKind == JsonValueKind.Object ? extra : default;
            int Count(string key, int limit)
            {
                if (signals.ValueKind != JsonValueKind.Object || !signals.TryGetProperty(key, out var value) ||
                    !value.TryGetInt32(out var number) || number < 0 || number > limit) return 0;
                return number;
            }
            bool article = signals.ValueKind == JsonValueKind.Object &&
                signals.TryGetProperty("isArticle", out var flag) && flag.ValueKind == JsonValueKind.True;
            string? language = signals.ValueKind == JsonValueKind.Object &&
                signals.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String
                    ? lang.GetString() : null;
            if (language?.Length > 24) language = null;
            return new BrowserPageResult {
                SessionId=sessionId, DocumentId=documentId,
                Browser=browser, Title=title, WindowTitle=windowTitle, Url=url,
                Selection=selection, Focused=focused, Incognito=false, TabId=tabId, WindowId=windowId,
                IsArticle=article, TableCount=Count("tableCount",100),
                LargestTableRows=Count("largestTableRows",5000),
                LargestTableColumns=Count("largestTableColumns",200),
                ImageCount=Count("imageCount",10000),
                CodeBlockCount=Count("codeBlockCount",10000),
                WordCount=Count("wordCount",1000000), Language=language
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { return null; }
    }
}
