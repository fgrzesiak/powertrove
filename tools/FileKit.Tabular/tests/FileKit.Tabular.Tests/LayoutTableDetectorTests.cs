namespace FileKit.Tabular.Tests;

public sealed class LayoutTableDetectorTests
{
    private static PositionedWord Word(string text, double x, double y, double width = 25) => new(text, x, y, x + width, y + 10);
    [Fact]
    public void RuledGridPreservesEmptyAndMultilineCellsInRowOrder()
    {
        var rules = Grid(10, 10, 2, 3); var words = new[] { Word("Header", 15, 85), Word("Value", 115, 85), Word("One", 15, 58), Word("line", 15, 45), Word("00123", 115, 58), Word("Last", 15, 25) };
        var table = Assert.Single(LayoutTableDetector.Detect(new(1, words, rules)));
        Assert.False(table.Inferred); Assert.Equal(3, table.Rows.Count); Assert.Equal("Header", table.Rows[0][0]); Assert.Equal("One\nline", table.Rows[1][0]); Assert.Equal("00123", table.Rows[1][1]); Assert.Equal("", table.Rows[2][1]);
    }
    [Fact]
    public void AlignedUnruledTablesNeedThreeRowsColumnsAndPersistentGutters()
    {
        var words = new List<PositionedWord>(); for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) words.Add(Word($"R{r}C{c}", 20 + c * 100, 100 - r * 20));
        var table = Assert.Single(LayoutTableDetector.Detect(new(1, words, []))); Assert.True(table.Inferred); Assert.Equal("R2C1", table.Rows[2][1]);
        Assert.Empty(LayoutTableDetector.Detect(new(1, words, []), false)); Assert.Empty(LayoutTableDetector.Detect(new(1, words.Where(w => w.Left < 200).ToArray(), [])));
        var ambiguous = words.Select((w, i) => i == 7 ? w with { Left = w.Left + 20, Right = w.Right + 20 } : w).ToArray();
        Assert.Throws<TableLayoutException>(() => LayoutTableDetector.Detect(new(1, ambiguous, [])));
    }
    [Fact]
    public void SeparateTablesAndAdjacentBorderFragmentsAreDetectedWithoutMerging()
    {
        var rules = Grid(10, 10, 2, 2).Concat(Grid(10, 200, 2, 2)).ToArray();
        var words = new[] { Word("A", 15, 55), Word("B", 115, 55), Word("C", 15, 25), Word("D", 115, 25), Word("E", 15, 245), Word("F", 115, 245), Word("G", 15, 215), Word("H", 115, 215) };
        var tables = LayoutTableDetector.Detect(new(2, words, rules)); Assert.Equal(2, tables.Count);
        Assert.Equal("E", tables[0].Rows[0][0]); Assert.Equal("A", tables[1].Rows[0][0]);
        var segmented = Grid(10, 10, 2, 2).SelectMany(rule => new[] { rule with { X2 = (rule.X1 + rule.X2) / 2, Y2 = (rule.Y1 + rule.Y2) / 2 }, rule with { X1 = (rule.X1 + rule.X2) / 2, Y1 = (rule.Y1 + rule.Y2) / 2 } }).ToArray();
        Assert.Single(LayoutTableDetector.Detect(new(1, words.Take(4).ToArray(), segmented)));
    }
    [Fact]
    public void MergedCellsCrossingTextAndInvalidGeometryFailExplicitly()
    {
        var grid = Grid(10, 10, 2, 2); var words = new[] { Word("A", 15, 55), Word("B", 115, 55) };
        Assert.Throws<TableLayoutException>(() => LayoutTableDetector.Detect(new(1, words, grid.Where(r => r.X1 != 110 || r.X2 != 110).Append(new(110, 10, 110, 40)).ToArray())));
        Assert.Throws<TableLayoutException>(() => LayoutTableDetector.Detect(new(1, [Word("cross", 100, 55, 30)], grid)));
        Assert.Throws<TableLayoutException>(() => LayoutTableDetector.Detect(new(1, [Word("bad", double.NaN, 1)], [])));
        Assert.Empty(LayoutTableDetector.Detect(new(1, [], [])));
        Assert.ThrowsAny<OperationCanceledException>(() => LayoutTableDetector.Detect(new(1, words, grid), ct: new(true)));
    }
    private static IReadOnlyList<TableRule> Grid(double x, double y, int columns, int rows)
    {
        var rules = new List<TableRule>();
        for (int r = 0; r <= rows; r++) rules.Add(new(x, y + r * 30, x + columns * 100, y + r * 30));
        for (int c = 0; c <= columns; c++) rules.Add(new(x + c * 100, y, x + c * 100, y + rows * 30)); return rules;
    }
}
