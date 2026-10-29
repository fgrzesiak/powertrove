namespace PowerTrove.Desktop;

/// <summary>Whole-image dimensions; no canvas, cropping, offsets or rotation.</summary>
internal sealed class ImageResizeDimensions(int originalWidth, int originalHeight, int width, int height)
{
    public int OriginalWidth { get; } = originalWidth;
    public int OriginalHeight { get; } = originalHeight;
    public int Width { get; private set; } = width;
    public int Height { get; private set; } = height;
    private double SliderScale => Math.Min(4, Math.Min(30000.0 / Math.Max(OriginalWidth, OriginalHeight), Math.Sqrt(60_000_000.0 / ((long)OriginalWidth * OriginalHeight))));
    public int SliderWidth => Math.Max(Width, Math.Max(1, (int)Math.Floor(OriginalWidth * SliderScale)));
    public int SliderHeight => Math.Max(Height, Math.Max(1, (int)Math.Floor(OriginalHeight * SliderScale)));
    public bool SetWidth(int value) => Set(value, Math.Max(1, (int)Math.Round(value * (double)OriginalHeight / OriginalWidth)));
    public bool SetHeight(int value) => Set(Math.Max(1, (int)Math.Round(value * (double)OriginalWidth / OriginalHeight)), value);
    public bool SetScale(double value) => double.IsFinite(value) && value > 0 && value <= 30000 &&
        Set(Math.Max(1, (int)Math.Round(OriginalWidth * value)), Math.Max(1, (int)Math.Round(OriginalHeight * value)));
    private bool Set(int w, int h)
    {
        if (w is < 1 or > 30000 || h is < 1 or > 30000 || (long)w * h > 60_000_000) return false;
        Width = w; Height = h; return true;
    }
}
