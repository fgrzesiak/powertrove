namespace FileKit.Tabular;

public sealed class TableLayoutException(string message) : Exception(message);
public sealed record PositionedWord(string Text, double Left, double Bottom, double Right, double Top);
public sealed record TableRule(double X1, double Y1, double X2, double Y2);
public sealed record PositionedPage(int Number, IReadOnlyList<PositionedWord> Words, IReadOnlyList<TableRule> Rules);
public sealed record DetectedTable(int Page, IReadOnlyList<IReadOnlyList<string>> Rows, bool Inferred, double Top = 0);

/// <summary>Conservative rectangular grid/whitespace table detection, independent of PDF parsing.</summary>
public static class LayoutTableDetector
{
    private const double Tolerance = 1.5;
    private sealed record Span(double Start, double End);
    public static IReadOnlyList<DetectedTable> Detect(PositionedPage page, bool inferUnruled = true, CancellationToken ct = default)
    {
        if (page.Number < 1 || page.Words.Count > 50000 || page.Rules.Count > 10000 || page.Words.Any(w => w.Text.Length > 32767 || !Valid(w.Left, w.Bottom, w.Right, w.Top) || w.Right <= w.Left || w.Top <= w.Bottom) || page.Rules.Any(r => !Valid(r.X1, r.Y1, r.X2, r.Y2))) throw new TableLayoutException("Invalid or excessive page geometry.");
        var tables = new List<DetectedTable>(); var consumed = new HashSet<PositionedWord>();
        var horizontal = Merge(page.Rules.Where(r => Math.Abs(r.Y1 - r.Y2) <= Tolerance && Math.Abs(r.X2 - r.X1) >= 20), false);
        var vertical = Merge(page.Rules.Where(r => Math.Abs(r.X1 - r.X2) <= Tolerance && Math.Abs(r.Y2 - r.Y1) >= 15), true);
        // Equal-span horizontal rules identify a full rectangular grid. Incomplete/merged grids are refused.
        var groups = new List<List<TableRule>>();
        foreach (var line in horizontal.OrderBy(r => r.X1).ThenBy(r => r.X2))
        {
            ct.ThrowIfCancellationRequested(); var group = groups.FirstOrDefault(g => Math.Abs(g[0].X1 - line.X1) <= Tolerance && Math.Abs(g[0].X2 - line.X2) <= Tolerance);
            if (group is null) groups.Add([line]); else group.Add(line);
        }
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested(); var allY = Cluster(group.Select(r => r.Y1)); if (allY.Count < 3) continue;
            // Separate tables sharing the same width when there is no connecting vertical rule.
            var blocks = new List<List<double>>();
            foreach (double y in allY)
            {
                if (blocks.Count == 0 || !vertical.Any(v => v.Y1 <= blocks[^1][^1] + Tolerance && v.Y2 >= y - Tolerance && v.X1 >= group[0].X1 - Tolerance && v.X1 <= group[0].X2 + Tolerance)) blocks.Add([y]);
                else blocks[^1].Add(y);
            }
            foreach (var ys in blocks)
            {
                if (ys.Count < 3) continue; double left = group[0].X1, right = group[0].X2, bottom = ys[0], top = ys[^1];
                var inside = page.Words.Where(w => CenterX(w) >= left && CenterX(w) <= right && CenterY(w) >= bottom && CenterY(w) <= top).ToArray(); if (inside.Length == 0) continue;
                var xs = Cluster(vertical.Where(v => v.Y1 <= bottom + Tolerance && v.Y2 >= top - Tolerance && v.X1 >= left - Tolerance && v.X1 <= right + Tolerance).Select(v => v.X1));
                if (xs.Count < 3 || Math.Abs(xs[0] - left) > Tolerance || Math.Abs(xs[^1] - right) > Tolerance) throw new TableLayoutException("Incomplete or merged table grid.");
                if (xs.Count > 129 || ys.Count > 10001 || (long)(xs.Count - 1) * (ys.Count - 1) > 100000) throw new TableLayoutException("Table dimensions exceed the supported limit.");
                if (vertical.Any(v => v.X1 > left + Tolerance && v.X1 < right - Tolerance && v.Y1 < top - Tolerance && v.Y2 > bottom + Tolerance && !xs.Any(x => Math.Abs(x - v.X1) <= Tolerance))) throw new TableLayoutException("Partial cell borders or merged cells are unsupported.");
                if (horizontal.Any(h => h.Y1 > bottom + Tolerance && h.Y1 < top - Tolerance && h.X1 < right - Tolerance && h.X2 > left + Tolerance && !ys.Any(y => Math.Abs(y - h.Y1) <= Tolerance))) throw new TableLayoutException("Partial row borders or merged cells are unsupported.");
                var cellWords = new Dictionary<int, List<PositionedWord>>();
                foreach (var word in inside)
                {
                    ct.ThrowIfCancellationRequested(); int column = Interval(xs, CenterX(word)), row = Interval(ys, CenterY(word));
                    if (column < 0 || row < 0 || word.Left < xs[column] - Tolerance || word.Right > xs[column + 1] + Tolerance || word.Bottom < ys[row] - Tolerance || word.Top > ys[row + 1] + Tolerance) throw new TableLayoutException("Text crosses table cell boundaries.");
                    int key = row * (xs.Count - 1) + column; if (!cellWords.TryGetValue(key, out var words)) cellWords[key] = words = [];
                    words.Add(word); if (!consumed.Add(word)) throw new TableLayoutException("Overlapping table regions.");
                }
                var rows = new List<IReadOnlyList<string>>();
                for (int row = ys.Count - 2; row >= 0; row--)
                {
                    ct.ThrowIfCancellationRequested(); var cells = new List<string>();
                    for (int column = 0; column < xs.Count - 1; column++)
                    {
                        cells.Add(Join(cellWords.GetValueOrDefault(row * (xs.Count - 1) + column) ?? []));
                    }
                    rows.Add(cells.AsReadOnly());
                }
                if (inside.Any(w => !consumed.Contains(w))) throw new TableLayoutException("Ambiguous table boundary text.");
                tables.Add(new(page.Number, rows.AsReadOnly(), false, top));
            }
        }
        if (inferUnruled)
        {
            var lines = TextLines(page.Words.Where(w => !consumed.Contains(w)));
            var candidates = new List<List<List<List<PositionedWord>>>>();
            foreach (var line in lines)
            {
                ct.ThrowIfCancellationRequested(); var cells = Split(line); if (cells.Count < 3) { candidates.Add([]); continue; }
                if (candidates.Count == 0 || candidates[^1].Count == 0 || candidates[^1][0].Count != cells.Count || Math.Abs(CenterY(candidates[^1][^1][0][0]) - CenterY(line[0])) > Math.Max(32, line.Max(w => w.Top - w.Bottom) * 3)) candidates.Add([]);
                candidates[^1].Add(cells);
            }
            foreach (var block in candidates.Where(b => b.Count >= 3))
            {
                int columns = block[0].Count; if (columns > 128 || (long)columns * block.Count > 100000) throw new TableLayoutException("Inferred table dimensions exceed the supported limit.");
                // Require persistent empty gutters and aligned data cells, not arbitrary text ordering.
                for (int col = 0; col < columns - 1; col++) if (block.Max(row => row[col].Max(w => w.Right)) + 12 > block.Min(row => row[col + 1].Min(w => w.Left))) throw new TableLayoutException("Unclear table column gutters.");
                for (int col = 0; col < columns; col++)
                {
                    var data = block.Skip(1).Select(row => row[col]).ToArray();
                    bool left = data.Max(c => c.Min(w => w.Left)) - data.Min(c => c.Min(w => w.Left)) <= 4;
                    bool right = data.Max(c => c.Max(w => w.Right)) - data.Min(c => c.Max(w => w.Right)) <= 4;
                    if ((!left && !right) || data.Any(c => c.Count > 8 || string.Join(' ', c.Select(w => w.Text)).Length > 100)) throw new TableLayoutException("Ambiguous unruled table alignment.");
                }
                var rows = block.Select(row => (IReadOnlyList<string>)row.Select(c => CheckText(string.Join(' ', c.Select(w => w.Text)))).ToArray()).ToArray();
                tables.Add(new(page.Number, rows, true, block[0].SelectMany(c => c).Max(w => w.Top)));
            }
        }
        if (tables.Count > 128) throw new TableLayoutException("Too many tables on the page.");
        return tables.OrderByDescending(t => t.Top).ToArray();
    }
    private static bool Valid(params double[] values) => values.All(v => double.IsFinite(v) && Math.Abs(v) <= 1000000);
    private static int Interval(List<double> borders, double value)
    {
        int index = borders.BinarySearch(value); if (index < 0) index = ~index - 1;
        return index >= 0 && index < borders.Count - 1 ? index : -1;
    }
    private static List<TableRule> Merge(IEnumerable<TableRule> rules, bool vertical)
    {
        var result = new List<TableRule>();
        var groups = new List<(double Axis, List<Span> Spans)>();
        foreach (var rule in rules.OrderBy(r => vertical ? r.X1 : r.Y1))
        {
            double axis = vertical ? (rule.X1 + rule.X2) / 2 : (rule.Y1 + rule.Y2) / 2;
            if (groups.Count == 0 || axis - groups[^1].Axis > Tolerance) groups.Add((axis, []));
            groups[^1].Spans.Add(new(vertical ? Math.Min(rule.Y1, rule.Y2) : Math.Min(rule.X1, rule.X2), vertical ? Math.Max(rule.Y1, rule.Y2) : Math.Max(rule.X1, rule.X2)));
        }
        foreach (var group in groups)
        {
            var spans = new List<Span>();
            foreach (var span in group.Spans.OrderBy(s => s.Start))
                if (spans.Count == 0 || span.Start > spans[^1].End + Tolerance) spans.Add(span); else spans[^1] = spans[^1] with { End = Math.Max(spans[^1].End, span.End) };
            foreach (var span in spans) result.Add(vertical ? new(group.Axis, span.Start, group.Axis, span.End) : new(span.Start, group.Axis, span.End, group.Axis));
        }
        return result;
    }
    private static double CenterX(PositionedWord w) => (w.Left + w.Right) / 2;
    private static double CenterY(PositionedWord w) => (w.Bottom + w.Top) / 2;
    private static List<double> Cluster(IEnumerable<double> values)
    {
        var result = new List<double>(); foreach (double value in values.Order()) if (result.Count == 0 || value - result[^1] > Tolerance) result.Add(value); return result;
    }
    private static List<List<PositionedWord>> TextLines(IEnumerable<PositionedWord> words)
    {
        var lines = new List<List<PositionedWord>>();
        foreach (var word in words.OrderByDescending(CenterY).ThenBy(w => w.Left))
        {
            if (lines.Count == 0 || Math.Abs(CenterY(lines[^1][0]) - CenterY(word)) > Math.Max(2, (word.Top - word.Bottom) * .3)) lines.Add([word]); else lines[^1].Add(word);
        }
        foreach (var line in lines) line.Sort((a, b) => a.Left.CompareTo(b.Left)); return lines;
    }
    private static List<List<PositionedWord>> Split(List<PositionedWord> words)
    {
        var cells = new List<List<PositionedWord>>();
        foreach (var word in words)
        {
            if (cells.Count == 0 || word.Left - cells[^1][^1].Right >= Math.Max(12, (word.Top - word.Bottom) * 1.2)) cells.Add([word]); else cells[^1].Add(word);
        }
        return cells;
    }
    private static string CheckText(string value) => value.Length <= 32767 ? value : throw new TableLayoutException("Table cell text limit exceeded.");
    private static string Join(IEnumerable<PositionedWord> words) => CheckText(string.Join('\n', TextLines(words).Select(line => string.Join(' ', line.Select(w => w.Text)))));
}
