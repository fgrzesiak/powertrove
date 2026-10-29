namespace PowerTrove.Desktop;

internal interface IVisualActionEditor
{
    HostedView HostView { get; }
    bool ReturnToOptions { get; }
    DictionaryParameters DraftParameters { get; }
    Task<DictionaryParameters?> ShowSelectionAsync();
}
