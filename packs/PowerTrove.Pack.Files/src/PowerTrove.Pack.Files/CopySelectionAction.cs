using System.Text.Json;

namespace PowerTrove.Pack.Files;

public sealed class CopySelectionAction : IAction
{
    private readonly bool _names;
    public CopySelectionAction(bool names) => _names = names;
    public ActionDescriptor Descriptor => new("powertrove.files." + (_names ? "copy-names" : "copy-paths"), SR.GetString(_names ? "CopyNames" : "CopyPaths"), SR.GetString("CopyDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, new[] { "copy", "paths", "names", "clipboard" }, false, false,
        Parameters: new[] { FileTools.Choice("Format", "lines", "lines", "quoted", "semicolon", "json-array") },
        Selection: new SelectionRequirements(new HashSet<string>(), MaxItems: 10000));
    private static bool Accept(ContextSnapshot s) => !s.IsStale && s.Status == ContextStatus.Available && s.Content is FileSelection f && f.Files.Count is > 0 and <= 10000 && f.Files.All(x => x.IsAvailableLocally && x.IsAccessible && !string.IsNullOrEmpty(x.Path));
    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return new(new ActionAvailability(Accept(context), Accept(context) ? null : SR.GetString("SelectionUnavailable"))); }
    public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return new(new ActionPreview(false, SR.GetString("CopyDescription"), [], [], !Accept(context))); }
    public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromResult(new ActionReceipt(false, true, [], [], false));
        if (!Accept(context)) return Task.FromResult(new ActionReceipt(false, false, [], new[] { new ActionFailure(null, SR.GetString("SelectionUnavailable")) }, false));
        string[] values = ((FileSelection)context.Content).Files.Select(f => _names ? f.Name : f.Path!).ToArray();
        string format = ActionParameterValues.From(parameters, Descriptor.Parameters).GetChoice("Format");
        string content = format switch
        {
            "quoted" => string.Join(" ", values.Select(Quote)),
            "semicolon" => string.Join(";", values),
            "json-array" => JsonSerializer.Serialize(values),
            _ => string.Join(Environment.NewLine, values)
        };
        return Task.FromResult(new ActionReceipt(true, false, [], [], false, SR.Count(_names ? "CopiedNames" : "CopiedPaths", values.Length), content));
    }
    // CommandLineToArgvW quoting, including terminal backslashes before the closing quote.
    private static string Quote(string value)
    {
        var result = new System.Text.StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
