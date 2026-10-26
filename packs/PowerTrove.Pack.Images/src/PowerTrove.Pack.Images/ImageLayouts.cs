using FileKit.Imaging.Raster;

namespace PowerTrove.Pack.Images;

public readonly record struct ImageSize(int Width, int Height);
public sealed record ImagePlacement(int Width, int Height, int X, int Y, RasterRect? Crop = null);
public sealed record ImageLayout(int Width, int Height, IReadOnlyList<ImagePlacement> Images);

public static class ImageLayouts
{
    public static ImageLayout Stitch(IReadOnlyList<ImageSize> sizes, bool horizontal, int gap, string align)
    {
        Validate(sizes, gap, 50);
        if (align is not ("start" or "center" or "end")) throw new RasterException("Invalid alignment.");
        int common = horizontal ? sizes.Min(s => s.Height) : sizes.Min(s => s.Width);
        var scaled = sizes.Select(s => horizontal ? new ImageSize(Math.Max(1, (int)Math.Round((double)s.Width * common / s.Height)), common)
            : new ImageSize(common, Math.Max(1, (int)Math.Round((double)s.Height * common / s.Width)))).ToArray();
        long length = scaled.Sum(s => horizontal ? (long)s.Width : s.Height) + (long)gap * (sizes.Count - 1);
        if (length > int.MaxValue) throw new RasterException("Canvas exceeds the supported limit.");
        int w = horizontal ? (int)length : common, h = horizontal ? common : (int)length;
        RasterImage.ByteLength(w, h); int at = 0;
        var placements = new List<ImagePlacement>();
        foreach (var size in scaled)
        {
            // The common dimension is identical, so all three cross-axis alignments coincide.
            placements.Add(new(size.Width, size.Height, horizontal ? at : 0, horizontal ? 0 : at));
            at += (horizontal ? size.Width : size.Height) + gap;
        }
        return new(w, h, placements);
    }
    public static ImageLayout Grid(IReadOnlyList<ImageSize> sizes, int columns, int cell, int gap, bool cover)
    {
        Validate(sizes, gap, 100);
        if (cell is < 100 or > 2000 || columns < 0 || columns > 100) throw new RasterException("Invalid grid options.");
        columns = columns == 0 ? (int)Math.Ceiling(Math.Sqrt(sizes.Count)) : Math.Min(columns, sizes.Count);
        int rows = (sizes.Count + columns - 1) / columns;
        int width = checked(columns * cell + (columns - 1) * gap), height = checked(rows * cell + (rows - 1) * gap);
        RasterImage.ByteLength(width, height);
        var placements = sizes.Select((s, i) =>
        {
            if (cover)
            {
                var rect = AspectCrop.Calculate(s.Width, s.Height, 1, "center");
                return new ImagePlacement(cell, cell, i % columns * (cell + gap), i / columns * (cell + gap), rect);
            }
            double scale = Math.Min((double)cell / s.Width, (double)cell / s.Height);
            int w = Math.Max(1, (int)Math.Round(s.Width * scale)), h = Math.Max(1, (int)Math.Round(s.Height * scale));
            return new ImagePlacement(w, h, i % columns * (cell + gap) + (cell - w) / 2, i / columns * (cell + gap) + (cell - h) / 2);
        }).ToArray();
        return new(width, height, placements);
    }
    private static void Validate(IReadOnlyList<ImageSize> sizes, int gap, int maximum)
    {
        if (sizes is null || sizes.Count < 2 || sizes.Count > maximum || gap < 0 || gap > 2000) throw new RasterException("Invalid layout options.");
        foreach (var s in sizes) RasterImage.ByteLength(s.Width, s.Height);
    }
}
