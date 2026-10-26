using System;
using Windows.Graphics;

namespace PowerTrove.Platform.Windows.Displays;

public static class PlacementCalculator
{
    public static RectInt32 CalculateCenterPlacement(double logicalWidth, double logicalHeight, MonitorWorkArea workArea)
    {
        // Scale to physical pixels using the monitor's DPI
        int physicalWidth = (int)(logicalWidth * workArea.ScaleFactorX);
        int physicalHeight = (int)(logicalHeight * workArea.ScaleFactorY);

        // Clamp to work area bounds
        if (physicalWidth > workArea.Width) physicalWidth = workArea.Width;
        if (physicalHeight > workArea.Height) physicalHeight = workArea.Height;

        // Center in the work area
        int x = workArea.X + (workArea.Width - physicalWidth) / 2;
        int y = workArea.Y + (workArea.Height - physicalHeight) / 2;

        return new RectInt32(x, y, physicalWidth, physicalHeight);
    }
    /// <summary>
    /// Launcher placement: horizontally centered, top edge at 20 % of the work area height, like
    /// Spotlight or PowerToys Run, so results grow downward and the bar stays where the eye expects it.
    /// </summary>
    public static RectInt32 CalculateLauncherPlacement(double logicalWidth, double logicalHeight, MonitorWorkArea workArea)
    {
        var centered = CalculateCenterPlacement(logicalWidth, logicalHeight, workArea);
        int marginX = Math.Min((int)Math.Round(12 * workArea.ScaleFactorX), Math.Max(0, (workArea.Width - 1) / 2));
        int marginY = Math.Min((int)Math.Round(12 * workArea.ScaleFactorY), Math.Max(0, (workArea.Height - 1) / 2));
        int width = Math.Min(centered.Width, workArea.Width - 2 * marginX);
        int height = Math.Min(centered.Height, workArea.Height - 2 * marginY);
        int x = workArea.X + (workArea.Width - width) / 2;
        int y = Math.Clamp(workArea.Y + (int)(workArea.Height * 0.2), workArea.Y + marginY, workArea.Y + workArea.Height - marginY - height);
        return new RectInt32(x, y, width, height);
    }

    /// <summary>Keeps a remembered window position fully inside the work area.</summary>
    public static RectInt32 ClampToWorkArea(int x, int y, int width, int height, MonitorWorkArea workArea)
    {
        width = Math.Min(width, workArea.Width);
        height = Math.Min(height, workArea.Height);
        x = Math.Clamp(x, workArea.X, workArea.X + workArea.Width - width);
        y = Math.Clamp(y, workArea.Y, workArea.Y + workArea.Height - height);
        return new RectInt32(x, y, width, height);
    }
}
