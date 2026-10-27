using System.Globalization;
using System.Text;
using System.Xml;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;

namespace FileKit.Imaging.Vector;

public sealed record VectorOptions(bool Monochrome = false, string Detail = "medium", int Threshold = 128);
public sealed record VectorResult(string Svg, int Colors, int Contours, int Segments);

/// <summary>Deterministic palette quantization and closed polygon contour tracing.
/// Output contains only SVG paths, never an embedded raster or external resources.</summary>
public static class RasterVectorizer
{
    public const int MaxSourcePixels = 16_000_000;
    public const int MaxEdges = 250_000;
    public const int MaxSvgBytes = 5 * 1024 * 1024;
    private readonly record struct Edge(int From, int To, int Direction);
    private readonly record struct Point(int X, int Y);
    public static VectorResult Trace(RasterImage source, VectorOptions options, CancellationToken ct = default)
    {
        if (options.Detail is not ("low" or "medium" or "high") || options.Threshold is < 0 or > 255 || (long)source.Width * source.Height > MaxSourcePixels) throw new RasterException("Unsupported vectorization options or source size.");
        ct.ThrowIfCancellationRequested();
        int side = options.Detail switch { "low" => 256, "high" => 1024, _ => 512 };
        double scale = Math.Min(1, (double)side / Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale)), height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var raster = scale < 1 ? RasterOperations.ResizeBilinear(source, width, height, ct) : new RasterImage(width, height, source.Bgra.ToArray());
        for (int i = 0; i < raster.Bgra.Length; i += 4)
        {
            if ((i & 65535) == 0) ct.ThrowIfCancellationRequested();
            if (raster.Bgra[i + 3] < 16) Array.Clear(raster.Bgra, i, 4);
        }
        IndexedRaster indexed;
        if (options.Monochrome)
        {
            var indices = new byte[width * height];
            for (int i = 0; i < indices.Length; i++)
            {
                if ((i & 16383) == 0) ct.ThrowIfCancellationRequested(); int p = i * 4;
                if (raster.Bgra[p + 3] < 128) continue;
                double luma = .0722 * raster.Bgra[p] + .7152 * raster.Bgra[p + 1] + .2126 * raster.Bgra[p + 2];
                indices[i] = luma < options.Threshold ? (byte)1 : (byte)2;
            }
            indexed = new(width, height, [new(0, 0, 0, 0), new(0, 0, 0), new(255, 255, 255)], indices);
        }
        else indexed = PaletteQuantizer.Quantize(raster, options.Detail switch { "low" => 8, "high" => 32, _ => 16 }, false, ct);
        var edges = indexed.Palette.Select(_ => new List<Edge>()).ToArray(); int edgeCount = 0, stride = width + 1;
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int color = indexed.Indices[y * width + x]; if (indexed.Palette[color].A == 0) continue;
                int vertex = y * stride + x;
                if (y == 0 || indexed.Indices[(y - 1) * width + x] != color) Add(color, vertex, vertex + 1, 0);
                if (x == width - 1 || indexed.Indices[y * width + x + 1] != color) Add(color, vertex + 1, vertex + stride + 1, 1);
                if (y == height - 1 || indexed.Indices[(y + 1) * width + x] != color) Add(color, vertex + stride + 1, vertex + stride, 2);
                if (x == 0 || indexed.Indices[y * width + x - 1] != color) Add(color, vertex + stride, vertex, 3);
            }
        }
        if (edgeCount == 0) throw new RasterException("No visible shapes were found.");
        int contours = 0, segments = 0, colors = 0;
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var xml = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false }))
        {
            xml.WriteStartElement("svg", "http://www.w3.org/2000/svg"); xml.WriteAttributeString("width", source.Width.ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("height", source.Height.ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("viewBox", $"0 0 {width.ToString(CultureInfo.InvariantCulture)} {height.ToString(CultureInfo.InvariantCulture)}");
            for (int color = 0; color < edges.Length; color++)
            {
                ct.ThrowIfCancellationRequested(); if (edges[color].Count == 0) continue;
                var list = edges[color]; var outgoing = new Dictionary<int, List<int>>(); var used = new bool[list.Count];
                for (int e = 0; e < list.Count; e++) { if (!outgoing.TryGetValue(list[e].From, out var choices)) outgoing[list[e].From] = choices = []; choices.Add(e); }
                var path = new StringBuilder();
                for (int start = 0; start < list.Count; start++)
                {
                    if (used[start]) continue; ct.ThrowIfCancellationRequested(); var points = new List<Point>(); int current = start, steps = 0;
                    while (true)
                    {
                        if ((++steps & 1023) == 0) ct.ThrowIfCancellationRequested();
                        if (used[current] || steps > list.Count) throw new RasterException("An invalid contour was detected.");
                        var edge = list[current]; used[current] = true; points.Add(new(edge.From % stride, edge.From / stride));
                        if (edge.To == list[start].From) break;
                        int next = -1, best = 5;
                        foreach (int candidate in outgoing.GetValueOrDefault(edge.To) ?? [])
                        {
                            if (used[candidate]) continue;
                            int turn = (list[candidate].Direction - edge.Direction + 4) % 4;
                            int rank = turn switch { 1 => 0, 0 => 1, 3 => 2, _ => 3 };
                            if (rank < best) { best = rank; next = candidate; }
                        }
                        if (next < 0) throw new RasterException("An open contour was detected."); current = next;
                    }
                    var corners = new List<Point>();
                    for (int p = 0; p < points.Count; p++)
                    {
                        Point previous = points[(p + points.Count - 1) % points.Count], point = points[p], next = points[(p + 1) % points.Count];
                        if ((point.X - previous.X) * (next.Y - point.Y) != (point.Y - previous.Y) * (next.X - point.X)) corners.Add(point);
                    }
                    if (corners.Count < 3) throw new RasterException("A degenerate contour was detected.");
                    contours++; segments += corners.Count;
                    for (int p = 0; p < corners.Count; p++) path.Append(p == 0 ? 'M' : 'L').Append(corners[p].X.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(corners[p].Y.ToString(CultureInfo.InvariantCulture));
                    path.Append('Z');
                }
                colors++; var fill = indexed.Palette[color]; xml.WriteStartElement("path");
                xml.WriteAttributeString("fill", $"#{fill.R:X2}{fill.G:X2}{fill.B:X2}"); xml.WriteAttributeString("fill-rule", "evenodd");
                if (fill.A != 255) xml.WriteAttributeString("fill-opacity", (fill.A / 255d).ToString("0.###", CultureInfo.InvariantCulture));
                xml.WriteAttributeString("d", path.ToString()); xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        string svg = text.ToString(); if (Encoding.UTF8.GetByteCount(svg) > MaxSvgBytes) throw new RasterException("Vector output size limit exceeded.");
        return new(svg, colors, contours, segments);
        void Add(int color, int from, int to, int direction)
        { if (++edgeCount > MaxEdges) throw new RasterException("Vector complexity limit exceeded. Reduce detail or simplify the input."); edges[color].Add(new(from, to, direction)); }
    }
}
