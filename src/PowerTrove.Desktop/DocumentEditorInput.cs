namespace PowerTrove.Desktop;

/// <summary>
/// Actions the host opens in <see cref="DocumentEditorView"/> instead of the options dialog. They answer
/// <see cref="DocumentPreviewRequest"/>s; the host never references their pack's types, only these stable IDs.
/// </summary>
internal static class DocumentEditorInput
{
    public const string MarkdownViewActionId = "powertrove.markdown.view";
    public const string MarkdownEditActionId = "powertrove.markdown.edit";

    /// <summary>False opens the viewer, true the editor side by side with the preview, null any other action normally.</summary>
    public static bool? OpensEditor(string actionId) => actionId switch
    {
        MarkdownViewActionId => false,
        MarkdownEditActionId => true,
        _ => null
    };
}
