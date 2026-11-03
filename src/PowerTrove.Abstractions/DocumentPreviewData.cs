using System;
using System.Collections.Generic;

namespace PowerTrove.Abstractions;

/// <summary>
/// A rendered, static HTML view of a selected document for the host's document viewer and editor.
/// The pack sanitizes the markup; the host still renders it without scripts, network access or
/// navigation. Local images are referenced as <see cref="ResourceBaseUri"/> plus the URL-encoded path
/// relative to the document's folder; the host serves only files that <see cref="Files.CompanionResources"/>
/// allows. Ephemeral: never log, persist or add to history.
/// </summary>
public sealed record DocumentPreviewData(string Html, string? Title = null)
{
    /// <summary>Characters of HTML; the escaped JSON line of a preview result must stay below 4 MiB.</summary>
    public const int MaximumHtmlLength = 600_000;
    public const int MaximumTitleLength = 256;

    /// <summary>Origin the host uses for the document and its local images. Never resolves on a network.</summary>
    public const string ResourceBaseUri = "https://document.powertrove.example/";

    public bool IsValid =>
        !string.IsNullOrEmpty(Html) && Html.Length <= MaximumHtmlLength && Html.IndexOf('\0') < 0 &&
        (Title is null || Title.Length <= MaximumTitleLength && Title.IndexOf('\0') < 0);

    public override string ToString() => "DocumentPreviewData { Payload = redacted }";
}

/// <summary>
/// Explicit, ephemeral request for a <see cref="DocumentPreviewData"/>. The host's editor may add the unsaved
/// text it shows; the action then renders that text instead of the file's content. Never store either value in
/// preferences, execution parameters, recipes or history; hosts refuse the draft in execute requests.
/// </summary>
public static class DocumentPreviewRequest
{
    public const string ParameterName = "PowerTrove.Preview.Document";
    public const string DraftParameterName = "PowerTrove.Preview.DocumentDraft";

    /// <summary>Characters of unsaved text; the escaped JSON line of a preview request must stay below 4 MiB.</summary>
    public const int MaximumDraftLength = 500_000;

    public static bool IsRequested(ActionParameters parameters) => parameters is DictionaryParameters values &&
        values.Values.TryGetValue(ParameterName, out var value) && value == "true";

    /// <summary>The editor's unsaved text, or null when the saved file is to be rendered.</summary>
    public static string? Draft(ActionParameters parameters) => IsRequested(parameters) && parameters is DictionaryParameters values &&
        values.Values.TryGetValue(DraftParameterName, out var draft) && draft.Length <= MaximumDraftLength ? draft : null;

    public static DictionaryParameters Create(IReadOnlyDictionary<string, string>? values = null, string? draft = null)
    {
        if (draft is not null && draft.Length > MaximumDraftLength) throw new ArgumentOutOfRangeException(nameof(draft));
        var result = new Dictionary<string, string>(values ?? new Dictionary<string, string>()) { [ParameterName] = "true" };
        result.Remove(DraftParameterName);
        if (draft is not null) result[DraftParameterName] = draft;
        return new DictionaryParameters(result);
    }
}
