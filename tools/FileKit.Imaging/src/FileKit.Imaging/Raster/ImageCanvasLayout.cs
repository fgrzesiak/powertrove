namespace FileKit.Imaging.Raster;

/// <summary>One image layer, centered on a transparent canvas with pixel offsets and clockwise rotation.</summary>
public sealed record ImageCanvasLayout(int CanvasWidth, int CanvasHeight, int ImageWidth, int ImageHeight,
    int OffsetX = 0, int OffsetY = 0, int Angle = 0)
{
    public const int MaximumDimension = 10000;
    public const long MaximumPixels = 40_000_000;
    public double CenterX => CanvasWidth / 2.0 + OffsetX;
    public double CenterY => CanvasHeight / 2.0 + OffsetY;
    public double Radians => Angle * Math.PI / 180;
    public double BoundsWidth => Math.Abs(ImageWidth * Math.Cos(Radians)) + Math.Abs(ImageHeight * Math.Sin(Radians));
    public double BoundsHeight => Math.Abs(ImageWidth * Math.Sin(Radians)) + Math.Abs(ImageHeight * Math.Cos(Radians));
    public bool IsClipped => CenterX - BoundsWidth / 2 < -.001 || CenterY - BoundsHeight / 2 < -.001
        || CenterX + BoundsWidth / 2 > CanvasWidth + .001 || CenterY + BoundsHeight / 2 > CanvasHeight + .001;

    public void Validate()
    {
        if (CanvasWidth is < 1 or > MaximumDimension || CanvasHeight is < 1 or > MaximumDimension
            || ImageWidth is < 1 or > MaximumDimension || ImageHeight is < 1 or > MaximumDimension
            || (long)CanvasWidth * CanvasHeight > MaximumPixels || (long)ImageWidth * ImageHeight > MaximumPixels
            || OffsetX is < -20000 or > 20000 || OffsetY is < -20000 or > 20000 || Angle is < -180 or > 180)
            throw new RasterException("Invalid image canvas geometry.");
    }

    public static ImageCanvasLayout Fit(int sourceWidth, int sourceHeight, int canvasWidth, int canvasHeight, int angle = 0)
    {
        if (sourceWidth < 1 || sourceHeight < 1) throw new RasterException("Invalid source dimensions.");
        double radians = angle * Math.PI / 180;
        double w = Math.Abs(sourceWidth * Math.Cos(radians)) + Math.Abs(sourceHeight * Math.Sin(radians));
        double h = Math.Abs(sourceWidth * Math.Sin(radians)) + Math.Abs(sourceHeight * Math.Cos(radians));
        double scale = Math.Min((double)canvasWidth / w, (double)canvasHeight / h);
        var result = new ImageCanvasLayout(canvasWidth, canvasHeight, Math.Max(1, (int)Math.Floor(sourceWidth * scale + 1e-7)),
            Math.Max(1, (int)Math.Floor(sourceHeight * scale + 1e-7)), Angle: angle);
        result.Validate(); return result;
    }

    public static ImageCanvasLayout Resolve(int sourceWidth, int sourceHeight, int canvasWidth = 0, int canvasHeight = 0,
        int imageWidth = 0, int imageHeight = 0, int offsetX = 0, int offsetY = 0, int angle = 0)
    {
        if (sourceWidth < 1 || sourceHeight < 1) throw new RasterException("Invalid source dimensions.");
        double scale = Math.Min(1, Math.Min(10000.0 / Math.Max(sourceWidth, sourceHeight), Math.Sqrt(MaximumPixels / ((double)sourceWidth * sourceHeight))));
        int cw = canvasWidth > 0 ? canvasWidth : Math.Max(1, (int)Math.Floor(sourceWidth * scale));
        int ch = canvasHeight > 0 ? canvasHeight : Math.Max(1, (int)Math.Floor(sourceHeight * scale));
        var fit = Fit(sourceWidth, sourceHeight, cw, ch, angle);
        int iw = imageWidth > 0 ? imageWidth : imageHeight > 0 ? Math.Max(1, (int)Math.Round(imageHeight * (double)sourceWidth / sourceHeight)) : fit.ImageWidth;
        int ih = imageHeight > 0 ? imageHeight : imageWidth > 0 ? Math.Max(1, (int)Math.Round(imageWidth * (double)sourceHeight / sourceWidth)) : fit.ImageHeight;
        var result = new ImageCanvasLayout(cw, ch, iw, ih, offsetX, offsetY, angle);
        result.Validate(); return result;
    }
}

public static class ImageCanvasRenderer
{
    /// <summary>One inverse affine sampling pass, with alpha-correct bilinear filtering and transparent clipping.</summary>
    public static RasterImage Render(RasterImage source, ImageCanvasLayout layout, CancellationToken ct = default)
    {
        layout.Validate(); ct.ThrowIfCancellationRequested();
        var pixels = new byte[RasterImage.ByteLength(layout.CanvasWidth, layout.CanvasHeight)];
        double cos = Math.Cos(layout.Radians), sin = Math.Sin(layout.Radians);
        for (int y = 0; y < layout.CanvasHeight; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < layout.CanvasWidth; x++)
            {
                double dx = x + .5 - layout.CenterX, dy = y + .5 - layout.CenterY;
                double sx = ((cos * dx + sin * dy) / layout.ImageWidth + .5) * source.Width - .5;
                double sy = ((-sin * dx + cos * dy) / layout.ImageHeight + .5) * source.Height - .5;
                if (sx < -.5 || sy < -.5 || sx > source.Width - .5 || sy > source.Height - .5) continue;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                double fx = sx - x0, fy = sy - y0, alpha = 0, b = 0, g = 0, r = 0;
                for (int row = 0; row < 2; row++)
                for (int col = 0; col < 2; col++)
                {
                    int px = x0 + col, py = y0 + row;
                    if (px < 0 || py < 0 || px >= source.Width || py >= source.Height) continue;
                    double weight = (col == 0 ? 1 - fx : fx) * (row == 0 ? 1 - fy : fy);
                    int i = (py * source.Width + px) * 4;
                    double a = source.Bgra[i + 3] * weight;
                    alpha += a; b += source.Bgra[i] * a; g += source.Bgra[i + 1] * a; r += source.Bgra[i + 2] * a;
                }
                int target = (y * layout.CanvasWidth + x) * 4;
                pixels[target + 3] = Byte(alpha);
                if (alpha > 0) { pixels[target] = Byte(b / alpha); pixels[target + 1] = Byte(g / alpha); pixels[target + 2] = Byte(r / alpha); }
            }
        }
        return new(layout.CanvasWidth, layout.CanvasHeight, pixels);
    }

    private static byte Byte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
