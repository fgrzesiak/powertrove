namespace FileKit.Imaging.Raster;

public sealed class RasterException : Exception
{
    public RasterException(string message) : base(message) { }
}

/// <summary>Unpremultiplied BGRA pixels. All allocations are bounded before multiplication.</summary>
public sealed record RasterImage
{
    public const long MaxPixels = 60_000_000;
    public const int MaxDimension = 30_000;
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    public RasterImage(int width, int height, byte[] bgra)
    {
        int length = ByteLength(width, height);
        if (bgra is null || bgra.Length != length) throw new RasterException("Invalid pixel buffer.");
        Width = width; Height = height; Bgra = bgra;
    }

    public static int ByteLength(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
            throw new RasterException("Raster dimensions exceed the supported limit.");
        return checked(width * height * 4);
    }
}

public readonly record struct BgraColor(byte B, byte G, byte R, byte A = 255);
public readonly record struct RasterRect(int X, int Y, int Width, int Height);

public static class RasterOperations
{
    public static RasterImage Crop(RasterImage source, RasterRect rect, CancellationToken ct = default)
    {
        ValidateRect(source, rect);
        var result = Allocate(rect.Width, rect.Height);
        for (int y = 0; y < rect.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            Buffer.BlockCopy(source.Bgra, ((y + rect.Y) * source.Width + rect.X) * 4,
                result.Bgra, y * rect.Width * 4, rect.Width * 4);
        }
        return result;
    }

    public static RasterImage Rotate90(RasterImage s, CancellationToken ct = default) => Remap(s, s.Height, s.Width, (x, y) => (y, s.Height - 1 - x), ct);
    public static RasterImage Rotate180(RasterImage s, CancellationToken ct = default) => Remap(s, s.Width, s.Height, (x, y) => (s.Width - 1 - x, s.Height - 1 - y), ct);
    public static RasterImage Rotate270(RasterImage s, CancellationToken ct = default) => Remap(s, s.Height, s.Width, (x, y) => (s.Width - 1 - y, x), ct);
    public static RasterImage FlipHorizontal(RasterImage s, CancellationToken ct = default) => Remap(s, s.Width, s.Height, (x, y) => (s.Width - 1 - x, y), ct);
    public static RasterImage FlipVertical(RasterImage s, CancellationToken ct = default) => Remap(s, s.Width, s.Height, (x, y) => (x, s.Height - 1 - y), ct);

    public static RasterImage Pad(RasterImage source, int width, int height, int x, int y, BgraColor color, CancellationToken ct = default)
    {
        if (x < 0 || y < 0 || (long)x + source.Width > width || (long)y + source.Height > height)
            throw new RasterException("The source does not fit the canvas.");
        var result = Canvas(width, height, color, ct);
        for (int row = 0; row < source.Height; row++)
        {
            ct.ThrowIfCancellationRequested();
            Buffer.BlockCopy(source.Bgra, row * source.Width * 4, result.Bgra, ((row + y) * width + x) * 4, source.Width * 4);
        }
        return result;
    }

    public static RasterImage Grayscale(RasterImage source, CancellationToken ct = default)
    {
        var result = Allocate(source.Width, source.Height);
        for (int i = 0; i < source.Bgra.Length; i += 4)
        {
            if ((i & 65535) == 0) ct.ThrowIfCancellationRequested();
            byte luma = (byte)Math.Clamp((int)Math.Round(0.0722 * source.Bgra[i] + 0.7152 * source.Bgra[i + 1] + 0.2126 * source.Bgra[i + 2]), 0, 255);
            result.Bgra[i] = result.Bgra[i + 1] = result.Bgra[i + 2] = luma;
            result.Bgra[i + 3] = source.Bgra[i + 3];
        }
        return result;
    }

    public static RasterImage Compose(IReadOnlyList<(RasterImage Image, int X, int Y)> layers, int width, int height, BgraColor background, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Count > 300) throw new RasterException("Too many layers.");
        foreach (var (s, x, y) in layers)
            if (s is null || x < 0 || y < 0 || (long)x + s.Width > width || (long)y + s.Height > height)
                throw new RasterException("A layer does not fit the canvas.");
        var result = Canvas(width, height, background, ct);
        foreach (var (s, x, y) in layers)
            for (int row = 0; row < s.Height; row++)
            {
                ct.ThrowIfCancellationRequested();
                for (int col = 0; col < s.Width; col++) Blend(s.Bgra, (row * s.Width + col) * 4, result.Bgra, ((row + y) * width + col + x) * 4);
            }
        return result;
    }

    public static RasterImage ResizeBilinear(RasterImage source, int width, int height, CancellationToken ct = default)
    {
        var result = Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            double sy = Math.Clamp((y + 0.5) * source.Height / height - 0.5, 0, source.Height - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, source.Height - 1);
            for (int x = 0; x < width; x++)
            {
                double sx = Math.Clamp((x + 0.5) * source.Width / width - 0.5, 0, source.Width - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, source.Width - 1);
                int a = (y0 * source.Width + x0) * 4, b = (y0 * source.Width + x1) * 4;
                int c = (y1 * source.Width + x0) * 4, d = (y1 * source.Width + x1) * 4;
                double fx = sx - x0, fy = sy - y0;
                double Interpolate(double av, double bv, double cv, double dv) => (av * (1 - fx) + bv * fx) * (1 - fy) + (cv * (1 - fx) + dv * fx) * fy;
                double alpha = Interpolate(source.Bgra[a + 3], source.Bgra[b + 3], source.Bgra[c + 3], source.Bgra[d + 3]);
                int target = (y * width + x) * 4;
                result.Bgra[target + 3] = ToByte(alpha);
                for (int channel = 0; channel < 3; channel++)
                    result.Bgra[target + channel] = alpha == 0 ? (byte)0 : ToByte(Interpolate(
                        source.Bgra[a + channel] * source.Bgra[a + 3], source.Bgra[b + channel] * source.Bgra[b + 3],
                        source.Bgra[c + channel] * source.Bgra[c + 3], source.Bgra[d + channel] * source.Bgra[d + 3]) / alpha);
            }
        }
        return result;
    }

    public static RasterImage Pixelate(RasterImage source, RasterRect rect, int blockSize, CancellationToken ct = default)
    {
        return PixelateRegions(source, new[] { (rect, blockSize) }, ct);
    }

    /// <summary>Pixelates multiple bounded regions with one output allocation; source pixels stay unchanged.</summary>
    public static RasterImage PixelateRegions(RasterImage source, IReadOnlyList<(RasterRect Rect, int BlockSize)> regions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count > 1024) throw new RasterException("Too many pixelation regions.");
        long area = 0;
        foreach (var (rect, blockSize) in regions)
        {
            ValidateRect(source, rect);
            if (blockSize < 1 || blockSize > MaxBlockSize) throw new RasterException("Invalid block size.");
            area += (long)rect.Width * rect.Height;
            if (area > RasterImage.MaxPixels * 8) throw new RasterException("Pixelation workload exceeds the supported limit.");
        }
        ct.ThrowIfCancellationRequested();
        var result = new RasterImage(source.Width, source.Height, (byte[])source.Bgra.Clone());
        foreach (var (rect, blockSize) in regions)
        for (int y = rect.Y; y < rect.Y + rect.Height; y += blockSize)
            for (int x = rect.X; x < rect.X + rect.Width; x += blockSize)
            {
                ct.ThrowIfCancellationRequested();
                int right = Math.Min(x + blockSize, rect.X + rect.Width), bottom = Math.Min(y + blockSize, rect.Y + rect.Height);
                long alpha = 0, blue = 0, green = 0, red = 0;
                for (int row = y; row < bottom; row++) for (int col = x; col < right; col++)
                {
                    int i = (row * source.Width + col) * 4, a = source.Bgra[i + 3];
                    alpha += a; blue += source.Bgra[i] * a; green += source.Bgra[i + 1] * a; red += source.Bgra[i + 2] * a;
                }
                byte ba = (byte)(alpha / ((right - x) * (bottom - y)));
                for (int row = y; row < bottom; row++) for (int col = x; col < right; col++)
                {
                    int i = (row * source.Width + col) * 4;
                    result.Bgra[i] = alpha == 0 ? (byte)0 : (byte)(blue / alpha);
                    result.Bgra[i + 1] = alpha == 0 ? (byte)0 : (byte)(green / alpha);
                    result.Bgra[i + 2] = alpha == 0 ? (byte)0 : (byte)(red / alpha); result.Bgra[i + 3] = ba;
                }
            }
        return result;
    }

    private const int MaxBlockSize = 30_000;

    public static RasterImage Compare(RasterImage first, RasterImage second, out int changedPixels, CancellationToken ct = default)
    {
        if (first.Width != second.Width || first.Height != second.Height)
            throw new RasterException("Raster dimensions must match for comparison.");

        var result = Allocate(first.Width, first.Height);
        changedPixels = 0;

        for (int i = 0; i < first.Bgra.Length; i += 4)
        {
            if ((i & 65535) == 0) ct.ThrowIfCancellationRequested();

            byte b1 = first.Bgra[i], g1 = first.Bgra[i + 1], r1 = first.Bgra[i + 2], a1 = first.Bgra[i + 3];
            byte b2 = second.Bgra[i], g2 = second.Bgra[i + 1], r2 = second.Bgra[i + 2], a2 = second.Bgra[i + 3];

            if (b1 != b2 || g1 != g2 || r1 != r2 || a1 != a2)
            {
                changedPixels++;
                // Changed pixels in red
                result.Bgra[i] = 0;
                result.Bgra[i + 1] = 0;
                result.Bgra[i + 2] = 255;
                result.Bgra[i + 3] = 255;
            }
            else
            {
                // Dimmed copy: reduce opacity (e.g., 20%) over white or keep original but reduce alpha
                // Dimmed means blending it over white with say 30% alpha
                int alpha = (a1 * 77) / 255; // 30%
                result.Bgra[i] = (byte)(b1 * alpha / 255 + 255 * (255 - alpha) / 255);
                result.Bgra[i + 1] = (byte)(g1 * alpha / 255 + 255 * (255 - alpha) / 255);
                result.Bgra[i + 2] = (byte)(r1 * alpha / 255 + 255 * (255 - alpha) / 255);
                result.Bgra[i + 3] = 255;
            }
        }
        return result;
    }

    public static RasterImage Canvas(int width, int height, BgraColor color, CancellationToken ct = default)
    {
        var result = Allocate(width, height);
        for (int i = 0; i < result.Bgra.Length; i += 4)
        {
            if ((i & 65535) == 0) ct.ThrowIfCancellationRequested();
            result.Bgra[i] = color.B; result.Bgra[i + 1] = color.G; result.Bgra[i + 2] = color.R; result.Bgra[i + 3] = color.A;
        }
        return result;
    }

    private static RasterImage Allocate(int w, int h) => new(w, h, new byte[RasterImage.ByteLength(w, h)]);
    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    private static void ValidateRect(RasterImage s, RasterRect r)
    {
        if (r.X < 0 || r.Y < 0 || r.Width <= 0 || r.Height <= 0 || (long)r.X + r.Width > s.Width || (long)r.Y + r.Height > s.Height)
            throw new RasterException("Invalid raster rectangle.");
    }
    private static RasterImage Remap(RasterImage s, int w, int h, Func<int, int, (int X, int Y)> map, CancellationToken ct)
    {
        var result = Allocate(w, h);
        for (int y = 0; y < h; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                var (sx, sy) = map(x, y);
                Buffer.BlockCopy(s.Bgra, (sy * s.Width + sx) * 4, result.Bgra, (y * w + x) * 4, 4);
            }
        }
        return result;
    }
    private static void Blend(byte[] s, int si, byte[] d, int di)
    {
        int sa = s[si + 3], da = d[di + 3];
        int alpha = sa * 255 + da * (255 - sa);
        for (int c = 0; c < 3; c++)
            d[di + c] = alpha == 0 ? (byte)0 : (byte)((s[si + c] * sa * 255 + d[di + c] * da * (255 - sa) + alpha / 2) / alpha);
        d[di + 3] = (byte)((alpha + 127) / 255);
    }
}
