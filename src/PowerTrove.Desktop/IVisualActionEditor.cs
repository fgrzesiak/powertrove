namespace PowerTrove.Desktop;

internal interface IVisualActionEditor
{
    HostedView HostView { get; }
    bool ReturnToOptions { get; }
    /// <summary>The installed pack rendered no preview; the editor returned to the options page.</summary>
    bool PreviewUnsupported => false;
    DictionaryParameters DraftParameters { get; }
    Task<DictionaryParameters?> ShowSelectionAsync();
}
