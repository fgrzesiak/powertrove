using System.Runtime.CompilerServices;

namespace PowerTrove.Desktop;

/// <summary>
/// Remembers installed actions whose pack renders no visual preview, for example a pack released before the
/// visual editors. Their editors fall back to the regular options page instead of waiting for a preview that
/// never arrives. A reloaded pack registers new action instances, which get a new chance.
/// </summary>
internal sealed class VisualEditorSupport
{
    private static readonly object Marker = new();
    private readonly ConditionalWeakTable<IAction, object> _unsupported = new();

    /// <summary>A pack without visual previews answers a preview request with a valid preview but no image.</summary>
    public static bool LacksImage(ActionPreview? preview) => preview is { IsBlocked: false, ImagePreview: null };

    public void MarkUnsupported(IAction action) => _unsupported.AddOrUpdate(action, Marker);

    public bool IsSupported(IAction? action) => action is not null && !_unsupported.TryGetValue(action, out _);
}
