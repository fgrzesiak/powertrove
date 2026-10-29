namespace FileKit.Imaging.Raster;

/// <summary>UI-independent editing math. Pointer deltas are canvas pixels, not screen pixels.</summary>
public sealed class ImageCanvasEditorState
{
    private readonly Stack<ImageCanvasLayout> undo = new(), redo = new();
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public ImageCanvasLayout Layout { get; private set; }
    public bool LockAspect { get; set; } = true;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public ImageCanvasEditorState(int width, int height, ImageCanvasLayout layout)
    {
        if (width < 1 || height < 1) throw new RasterException("Invalid source dimensions.");
        layout.Validate(); SourceWidth = width; SourceHeight = height; Layout = layout;
    }

    public bool Set(ImageCanvasLayout value, bool history = true)
    {
        try { value.Validate(); } catch (RasterException) { return false; }
        if (value == Layout) return true;
        if (history) { Remember(Layout); redo.Clear(); }
        Layout = value; return true;
    }
    public bool SetImageWidth(int width) => Set(Layout with { ImageWidth = width,
        ImageHeight = LockAspect ? Math.Max(1, (int)Math.Round(Layout.ImageHeight * (double)width / Layout.ImageWidth)) : Layout.ImageHeight });
    public bool SetImageHeight(int height) => Set(Layout with { ImageHeight = height,
        ImageWidth = LockAspect ? Math.Max(1, (int)Math.Round(Layout.ImageWidth * (double)height / Layout.ImageHeight)) : Layout.ImageWidth });
    public bool FitImage() => Set(ImageCanvasLayout.Fit(SourceWidth, SourceHeight, Layout.CanvasWidth, Layout.CanvasHeight, Layout.Angle));
    public bool FitCanvas() => Set(Layout with { CanvasWidth = (int)Math.Ceiling(Layout.BoundsWidth - 1e-7),
        CanvasHeight = (int)Math.Ceiling(Layout.BoundsHeight - 1e-7), OffsetX = 0, OffsetY = 0 });
    public bool Rotate(int degrees) => Set(Layout with { Angle = Normalize(Layout.Angle + degrees) });
    public bool Align(int horizontal, int vertical) => Set(Layout with {
        OffsetX = horizontal == 2 ? Layout.OffsetX : (int)Math.Round(horizontal * (Layout.CanvasWidth - Layout.BoundsWidth) / 2),
        OffsetY = vertical == 2 ? Layout.OffsetY : (int)Math.Round(vertical * (Layout.CanvasHeight - Layout.BoundsHeight) / 2) });
    public bool Reset() => Set(ImageCanvasLayout.Resolve(SourceWidth, SourceHeight));

    public bool Drag(ImageCanvasLayout start, int horizontal, int vertical, double dx, double dy, bool canvas = false, bool constrain = false)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || Math.Abs(dx) > 40000 || Math.Abs(dy) > 40000) return false;
        if (canvas)
        {
            double canvasWidth = Math.Max(1, start.CanvasWidth + horizontal * dx), canvasHeight = Math.Max(1, start.CanvasHeight + vertical * dy);
            if (constrain)
            {
                double factor = ProportionalScale(start.CanvasWidth, start.CanvasHeight, horizontal, vertical, dx, dy);
                canvasWidth = start.CanvasWidth * factor; canvasHeight = start.CanvasHeight * factor;
            }
            return Set(start with { CanvasWidth = Math.Max(1, (int)Math.Round(canvasWidth)), CanvasHeight = Math.Max(1, (int)Math.Round(canvasHeight)) }, false);
        }
        if (constrain && horizontal == 0 && vertical == 0)
        { if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0; else dx = 0; }
        if (horizontal == 0 && vertical == 0) return Set(start with { OffsetX = start.OffsetX + (int)Math.Round(dx), OffsetY = start.OffsetY + (int)Math.Round(dy) }, false);
        double cos = Math.Cos(start.Radians), sin = Math.Sin(start.Radians);
        double localX = cos * dx + sin * dy, localY = -sin * dx + cos * dy;
        double width = Math.Max(1, start.ImageWidth + horizontal * localX), height = Math.Max(1, start.ImageHeight + vertical * localY);
        if (LockAspect || constrain)
        {
            double factor = ProportionalScale(start.ImageWidth, start.ImageHeight, horizontal, vertical, localX, localY);
            width = start.ImageWidth * factor; height = start.ImageHeight * factor;
        }
        int w = Math.Max(1, (int)Math.Round(width)), h = Math.Max(1, (int)Math.Round(height));
        double shiftX = horizontal * (w - start.ImageWidth) / 2.0, shiftY = vertical * (h - start.ImageHeight) / 2.0;
        return Set(start with { ImageWidth = w, ImageHeight = h,
            OffsetX = start.OffsetX + (int)Math.Round(cos * shiftX - sin * shiftY),
            OffsetY = start.OffsetY + (int)Math.Round(sin * shiftX + cos * shiftY) }, false);
    }

    private static double ProportionalScale(int width, int height, int horizontal, int vertical, double dx, double dy)
    {
        // Project a corner's motion onto its resize diagonal. Choosing the larger axis jumps when
        // opposed horizontal/vertical movements exchange dominance during one held gesture.
        double delta = horizontal == 0 ? vertical * dy / height : vertical == 0 ? horizontal * dx / width
            : (horizontal * dx * width + vertical * dy * height) / ((double)width * width + (double)height * height);
        return Math.Max(Math.Max(1.0 / width, 1.0 / height), 1 + delta);
    }

    public void FinishGesture(ImageCanvasLayout start)
    { if (start != Layout) { Remember(start); redo.Clear(); } }
    public void Undo() { if (undo.TryPop(out var value)) { redo.Push(Layout); Layout = value; } }
    public void Redo() { if (redo.TryPop(out var value)) { Remember(Layout); Layout = value; } }
    public static int Normalize(int angle) => ((angle + 180) % 360 + 360) % 360 - 180;
    public static int SnapAngle(int angle) => Normalize((int)Math.Round(angle / 15.0) * 15);
    private void Remember(ImageCanvasLayout value)
    {
        // Bound retained geometry only; image pixels never enter undo history.
        if (undo.Count >= 50) { var latest = undo.Take(49).Reverse().ToArray(); undo.Clear(); foreach (var item in latest) undo.Push(item); }
        undo.Push(value);
    }
}
