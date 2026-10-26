using System.Buffers.Binary;
using FileKit.Imaging.Raster;

namespace FileKit.Imaging.Png;

public sealed record IndexedRaster(int Width, int Height, IReadOnlyList<BgraColor> Palette, byte[] Indices);

/// <summary>Deterministic RGBA median cut, with a bounded histogram and optional error diffusion.</summary>
public static class PaletteQuantizer
{
    private sealed record Sample(uint Color, long Count)
    {
        public int Channel(int c) => (byte)(Color >> (c * 8));
    }

    public static IndexedRaster Quantize(RasterImage image, int colors = 256, bool dither = true, CancellationToken ct = default)
    {
        if (colors is < 2 or > 256) throw new RasterException("Invalid palette size.");
        var histogram = new Dictionary<uint, long>();
        bool reduced = false;
        for (int i = 0; i < image.Bgra.Length; i += 4)
        {
            if ((i & 65535) == 0) ct.ThrowIfCancellationRequested();
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(image.Bgra.AsSpan(i));
            if (reduced) key = Bucket(key);
            histogram[key] = histogram.GetValueOrDefault(key) + 1;
            if (!reduced && histogram.Count > 65536)
            {
                var bounded = new Dictionary<uint, long>();
                foreach (var (value, count) in histogram) bounded[Bucket(value)] = bounded.GetValueOrDefault(Bucket(value)) + count;
                histogram = bounded; reduced = true;
            }
        }

        var boxes = new List<List<Sample>> { histogram.OrderBy(k => k.Key).Select(k => new Sample(k.Key, k.Value)).ToList() };
        while (boxes.Count < colors)
        {
            ct.ThrowIfCancellationRequested();
            int index = -1, splitChannel = 0; long bestScore = -1;
            for (int b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count < 2) continue;
                for (int c = 0; c < 4; c++)
                {
                    int range = boxes[b].Max(s => s.Channel(c)) - boxes[b].Min(s => s.Channel(c));
                    long score = range * boxes[b].Sum(s => s.Count);
                    if (score > bestScore) { bestScore = score; index = b; splitChannel = c; }
                }
            }
            if (index < 0) break;
            var sorted = boxes[index].OrderBy(s => s.Channel(splitChannel)).ThenBy(s => s.Color).ToList();
            long half = sorted.Sum(s => s.Count) / 2, sum = 0; int cut = 0;
            do { sum += sorted[cut++].Count; } while (cut < sorted.Count - 1 && sum < half);
            boxes[index] = sorted.Take(cut).ToList(); boxes.Add(sorted.Skip(cut).ToList());
        }
        var palette = boxes.Select(box =>
        {
            long count = box.Sum(s => s.Count);
            byte Avg(int c) => (byte)((box.Sum(s => s.Channel(c) * s.Count) + count / 2) / count);
            return new BgraColor(Avg(0), Avg(1), Avg(2), Avg(3));
        }).ToArray();

        var indices = new byte[image.Width * image.Height];
        var current = new double[(image.Width + 2) * 4];
        var next = new double[current.Length];
        var cache = new Dictionary<uint, byte>();
        for (int y = 0; y < image.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++)
            {
                int i = (y * image.Width + x) * 4, e = (x + 1) * 4;
                double b = Math.Clamp(image.Bgra[i] + (dither ? current[e] : 0), 0, 255);
                double g = Math.Clamp(image.Bgra[i + 1] + (dither ? current[e + 1] : 0), 0, 255);
                double r = Math.Clamp(image.Bgra[i + 2] + (dither ? current[e + 2] : 0), 0, 255);
                double a = Math.Clamp(image.Bgra[i + 3] + (dither ? current[e + 3] : 0), 0, 255);
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(image.Bgra.AsSpan(i));
                byte nearest;
                if (!dither && cache.TryGetValue(key, out byte cached)) nearest = cached;
                else
                {
                    int best = 0; double distance = double.MaxValue;
                    for (int p = 0; p < palette.Length; p++)
                    {
                        var c = palette[p];
                        double diff = Math.Pow(b - c.B, 2) + Math.Pow(g - c.G, 2) + Math.Pow(r - c.R, 2) + Math.Pow(a - c.A, 2);
                        if (diff < distance) { distance = diff; best = p; }
                    }
                    nearest = (byte)best;
                    if (!dither && cache.Count < 65536) cache[key] = nearest;
                }
                indices[y * image.Width + x] = nearest;
                if (dither)
                {
                    var c = palette[nearest];
                    Diffuse(0, b - c.B); Diffuse(1, g - c.G); Diffuse(2, r - c.R); Diffuse(3, a - c.A);
                    void Diffuse(int ch, double error)
                    {
                        current[e + 4 + ch] += error * 7 / 16;
                        next[e - 4 + ch] += error * 3 / 16; next[e + ch] += error * 5 / 16; next[e + 4 + ch] += error / 16;
                    }
                }
            }
            (current, next) = (next, current); Array.Clear(next);
        }
        return new IndexedRaster(image.Width, image.Height, palette, indices);
    }

    private static uint Bucket(uint color) => (color & 0xF0F0F0F0u) | ((color & 0xF0F0F0F0u) >> 4);
}
