namespace FileKit.Pdf;

/// <summary>
/// Groups positioned words into lines and blocks and puts the blocks in reading order. Lines end at gaps wider than
/// a font size, so a column gutter splits them. Blocks join lines that follow closely below with an overlapping extent
/// and a similar size. The order comes from recursive XY cuts: horizontal bands from top to bottom, and inside a band
/// the columns from left to right. Consecutive bands that share a gutter are one column area, so paragraphs whose
/// gaps happen to line up across columns are not interleaved.
/// </summary>
internal static class PdfReadingOrder
{
    private const int MaximumDepth = 64;

    public static IReadOnlyList<PdfLayoutBlock> Blocks(IReadOnlyList<PdfLayoutWord> words, CancellationToken ct)
    {
        if (words.Count == 0) return [];
        var lines = Lines(words, ct);
        var blocks = Group(lines, ct);
        var ordered = new List<Box>(blocks.Count);
        Order(blocks, ordered, 0, ct);
        return ordered.Select(box => box.Block).ToArray();
    }

    private sealed class Box(PdfLayoutBlock block)
    {
        public PdfLayoutBlock Block { get; } = block;
        public double Left { get; } = block.Lines.Min(l => l.Words.Min(w => w.Left));
        public double Right { get; } = block.Lines.Max(l => l.Words.Max(w => w.Right));
        public double Bottom { get; } = block.Lines.Min(l => l.Words.Min(w => w.Bottom));
        public double Top { get; } = block.Lines.Max(l => l.Words.Max(w => w.Top));
    }

    private sealed class Row
    {
        public readonly List<PdfLayoutWord> Words = [];
        public double Bottom, Top;
    }

    private sealed class Line(PdfLayoutWord[] words)
    {
        public PdfLayoutWord[] Words { get; } = words;
        public double Left { get; } = words[0].Left;
        public double Right { get; } = words.Max(w => w.Right);
        public double Bottom { get; } = words.Min(w => w.Bottom);
        public double Top { get; } = words.Max(w => w.Top);
        public double Size { get; } = words.Select(w => w.FontSize).Order().ElementAt(words.Length / 2);
    }

    /// <summary>
    /// Rows of vertically overlapping words, split at gutters: a gap wider than a font size that the rows just above
    /// and below leave empty too. Gaps in justified lines are covered by their neighbours and do not split; an isolated
    /// row splits only at gaps of two font sizes.
    /// </summary>
    private static List<Line> Lines(IReadOnlyList<PdfLayoutWord> words, CancellationToken ct)
    {
        var rows = new List<Row>();
        foreach (var word in words.OrderByDescending(w => w.Top).ThenBy(w => w.Left))
        {
            ct.ThrowIfCancellationRequested();
            Row? match = null;
            // Rows are created top-down, so a word can only belong to one of the most recent rows.
            for (int i = rows.Count - 1; i >= 0 && i >= rows.Count - 8; i--)
            {
                var row = rows[i];
                double overlap = Math.Min(row.Top, word.Top) - Math.Max(row.Bottom, word.Bottom);
                if (overlap >= .5 * Math.Min(row.Top - row.Bottom, word.Top - word.Bottom)) { match = row; break; }
            }
            if (match is null)
            {
                match = new Row { Bottom = word.Bottom, Top = word.Top };
                rows.Add(match);
            }
            match.Words.Add(word);
        }
        foreach (var row in rows) row.Words.Sort((x, y) => x.Left.CompareTo(y.Left));
        var lines = new List<Line>();
        for (int r = 0; r < rows.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            var sorted = rows[r].Words;
            int start = 0;
            for (int i = 1; i <= sorted.Count; i++)
            {
                if (i < sorted.Count && !IsGutter(rows, r, sorted[i - 1], sorted[i])) continue;
                lines.Add(new Line(sorted.GetRange(start, i - start).ToArray()));
                start = i;
            }
        }
        return lines;
    }

    /// <summary>
    /// A gap is a gutter when part of it, at least most of a font size wide, stays empty in the rows nearby: the end of
    /// a short last line leaves its gap open only up to the column edge, while the gutter beyond it stays free.
    /// </summary>
    private static bool IsGutter(List<Row> rows, int index, PdfLayoutWord before, PdfLayoutWord after)
    {
        double size = Math.Max(1, Math.Max(before.FontSize, after.FontSize));
        if (after.Left - before.Right <= size) return false;
        var free = new List<(double From, double To)> { (before.Right, after.Left) };
        bool neighbours = false;
        for (int r = Math.Max(0, index - 3); r <= Math.Min(rows.Count - 1, index + 3) && free.Count > 0; r++)
        {
            if (r == index) continue;
            var row = rows[r];
            double distance = Math.Max(row.Bottom - rows[index].Top, rows[index].Bottom - row.Top);
            if (distance > size * 2.5) continue;
            neighbours = true;
            foreach (var word in row.Words)
            {
                var next = new List<(double, double)>();
                foreach (var (from, to) in free)
                {
                    if (word.Right <= from || word.Left >= to) { next.Add((from, to)); continue; }
                    if (word.Left > from) next.Add((from, word.Left));
                    if (word.Right < to) next.Add((word.Right, to));
                }
                free = next;
            }
        }
        if (!neighbours) return after.Left - before.Right > size * 2;
        return free.Any(f => f.To - f.From >= size * .8);
    }

    /// <summary>Each line joins the block whose last line is closest above it, if it follows like the next line of a paragraph.</summary>
    private static List<Box> Group(List<Line> lines, CancellationToken ct)
    {
        var open = new List<List<Line>>();
        foreach (var line in lines.OrderByDescending(l => l.Top).ThenBy(l => l.Left))
        {
            ct.ThrowIfCancellationRequested();
            List<Line>? best = null;
            double bestGap = double.MaxValue;
            foreach (var block in open)
            {
                var last = block[^1];
                double size = Math.Max(1, Math.Max(last.Size, line.Size));
                double gap = last.Bottom - line.Top;
                if (gap < -size * .4 || gap > size * .9) continue;
                if (Math.Abs(last.Size - line.Size) > size * .15) continue;
                if (Math.Min(last.Right, line.Right) - Math.Max(last.Left, line.Left) <= 0) continue;
                if (gap < bestGap) { best = block; bestGap = gap; }
            }
            if (best is null) open.Add([line]);
            else best.Add(line);
        }
        return open.Select(block => new Box(new PdfLayoutBlock(block.Select(l => new PdfLayoutLine(l.Words)).ToArray()))).ToList();
    }

    private static void Order(List<Box> boxes, List<Box> output, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (boxes.Count <= 1 || depth > MaximumDepth)
        {
            output.AddRange(boxes.OrderByDescending(b => b.Top).ThenBy(b => b.Left));
            return;
        }
        var bands = Split(boxes, horizontal: true);
        if (bands.Count == 1)
        {
            Columns(boxes, output, depth, ct);
            return;
        }
        // Bands that are split by the same gutter belong to one column area.
        int start = 0;
        for (int i = 1; i <= bands.Count; i++)
        {
            if (i < bands.Count && SharesGutter(bands[i - 1], bands[i])) continue;
            if (i - start == 1) Order(bands[start], output, depth + 1, ct);
            else Columns(bands.Skip(start).Take(i - start).SelectMany(b => b).ToList(), output, depth, ct);
            start = i;
        }
    }

    private static void Columns(List<Box> boxes, List<Box> output, int depth, CancellationToken ct)
    {
        var columns = Split(boxes, horizontal: false);
        if (columns.Count == 1) output.AddRange(boxes.OrderByDescending(b => b.Top).ThenBy(b => b.Left));
        else foreach (var column in columns) Order(column, output, depth + 1, ct);
    }

    private static bool SharesGutter(List<Box> upper, List<Box> lower)
    {
        var a = Gutters(upper);
        if (a.Count == 0) return false;
        var b = Gutters(lower);
        return a.Any(x => b.Any(y => Math.Min(x.To, y.To) - Math.Max(x.From, y.From) > 0));
    }

    private static List<(double From, double To)> Gutters(List<Box> boxes)
    {
        var gutters = new List<(double, double)>();
        double reach = double.NegativeInfinity;
        foreach (var box in boxes.OrderBy(b => b.Left))
        {
            if (reach > double.NegativeInfinity && box.Left > reach) gutters.Add((reach, box.Left));
            reach = Math.Max(reach, box.Right);
        }
        return gutters;
    }

    /// <summary>Splits at every gap in the projection: top to bottom for bands, left to right for columns.</summary>
    private static List<List<Box>> Split(List<Box> boxes, bool horizontal)
    {
        var sorted = horizontal ? boxes.OrderByDescending(b => b.Top).ToList() : boxes.OrderBy(b => b.Left).ToList();
        var parts = new List<List<Box>> { new() { sorted[0] } };
        double reach = horizontal ? sorted[0].Bottom : sorted[0].Right;
        for (int i = 1; i < sorted.Count; i++)
        {
            var box = sorted[i];
            bool gap = horizontal ? box.Top < reach : box.Left > reach;
            if (gap) parts.Add([]);
            parts[^1].Add(box);
            reach = horizontal ? Math.Min(reach, box.Bottom) : Math.Max(reach, box.Right);
            if (gap) reach = horizontal ? box.Bottom : box.Right;
        }
        return parts;
    }
}
