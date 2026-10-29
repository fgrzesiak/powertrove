using PowerTrove.Platform.Windows.Displays;
using Windows.Foundation;
using Windows.Graphics;

namespace PowerTrove.Desktop;

internal static class VisualEditorPlacement
{
    /// <summary>Bounds for a hosted view of <paramref name="logicalSize"/>, centered on the anchor window's monitor.</summary>
    public static RectInt32 Center(Size logicalSize, nint anchorWindow)
    {
        // Use the invoking palette's (or source window's) monitor, never the foreground after the view is created.
        var area = new WindowsMonitorService().GetMonitorWorkAreaForWindow(anchorWindow);
        return PlacementCalculator.CalculateCenterPlacement(logicalSize.Width, logicalSize.Height, area);
    }
}
