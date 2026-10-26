namespace ContextBar.Pack.Data;

public sealed class JsonValidateAction : IAction
{
    private readonly FileActionServices _services;
    public JsonValidateAction(FileActionServices services) => _services = services;
    public ActionDescriptor Descriptor => new("contextbar.data.json-validate", SR.Get("json-validate"), SR.Get("Description"), new HashSet<ContentKind> { ContentKind.Files }, new[] { "json", "validate" }, false, false, Selection: SelectionRequirements.Any("text"));
    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return new(new ActionAvailability(!context.IsStale && context.Status == ContextStatus.Available && context.Content is FileSelection f && f.Files.Count <= 1000 && f.Files.Any(x => !x.IsDirectory && x.IsAccessible && x.IsAvailableLocally && x.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)))); }
    public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return new(new ActionPreview(false, SR.Get("json-validate"), Array.Empty<string>(), Array.Empty<string>(), context.IsStale)); }
    public async Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken ct)
    {
        var failures = new List<ActionFailure>(); int valid = 0;
        if (context.IsStale || context.Status != ContextStatus.Available || context.Content is not FileSelection files || files.Files.Count > 1000) return new(false, false, [], new[] { new ActionFailure(null, SR.Get("Unavailable")) }, false);
        try
        {
            foreach (var file in files.Files.Where(f => !f.IsDirectory && f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)))
            {
                ct.ThrowIfCancellationRequested();
                try { await using var stream = _services.FileSystem.OpenRead(file.Path!); using var doc = await DataSupport.ReadJson(stream, ct).ConfigureAwait(false); valid++; }
                catch (FileProcessingException ex) { failures.Add(new(null, ex.Message)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(new(null, SR.Get("ReadError"))); }
            }
            return new(valid > 0 && failures.Count == 0, false, [], failures, false, string.Format(SR.Get("ValidJson"), valid));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(false, true, [], failures, false); }
    }
}
