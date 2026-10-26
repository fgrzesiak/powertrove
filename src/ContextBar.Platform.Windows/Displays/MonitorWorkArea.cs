using System;

namespace ContextBar.Platform.Windows.Displays;

public readonly record struct MonitorWorkArea(
    int X,
    int Y,
    int Width,
    int Height,
    int DpiX,
    int DpiY)
{
    public double ScaleFactorX => DpiX / 96.0;
    public double ScaleFactorY => DpiY / 96.0;
}
