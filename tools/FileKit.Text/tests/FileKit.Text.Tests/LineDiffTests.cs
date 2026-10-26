namespace FileKit.Text.Tests;

public sealed class LineDiffTests
{
    [Theory]
    [InlineData("", "", 0, 0, 0)]
    [InlineData("a\nb", "a\nb", 0, 0, 2)]
    [InlineData("a\nc", "a\nb\nc", 1, 0, 2)]
    [InlineData("a\nb\nc", "a\nc", 0, 1, 2)]
    [InlineData("a\nb", "a\nc", 1, 1, 1)]
    [InlineData("", "a\nb", 2, 0, 0)]
    [InlineData("a\nb", "", 0, 2, 0)]
    public void ExactCountsAndReconstruction(string before, string after, int added, int removed, int unchanged)
    {
        var diff = LineDiff.Compare(before, after);
        Assert.Equal(added, diff.Added); Assert.Equal(removed, diff.Removed); Assert.Equal(unchanged, diff.Unchanged);
        Assert.Equal(before, string.Join("\n", diff.Lines.Where(l => l.Kind != EditKind.Added).Select(l => l.Text)));
        Assert.Equal(after, string.Join("\n", diff.Lines.Where(l => l.Kind != EditKind.Removed).Select(l => l.Text)));
    }
    [Fact]
    public void CrLfOptionChangesComparison()
    {
        Assert.Equal(0, LineDiff.Compare("a\r\nb\r\n", "a\nb\n").Added);
        var exact = LineDiff.Compare("a\r\nb\r\n", "a\nb\n", false);
        Assert.Equal(2, exact.Added); Assert.Equal(2, exact.Removed);
        Assert.Equal(1, LineDiff.Compare("a\n", "a", false).Added);
    }
    [Fact]
    public async Task HtmlEncodesInputAndIncludesNoActiveOrExternalResources()
    {
        var report = new StringWriter(); await LineDiff.WriteHtmlAsync(report, LineDiff.Compare("<script>alert('x')</script>", "<&>"), new("Compare", "Added", "Removed", "Unchanged"));
        string html = report.ToString(); Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("<script", html); Assert.DoesNotContain("src=", html); Assert.DoesNotContain("href=", html); Assert.Contains("default-src 'none'", html);
    }
    [Fact]
    public async Task InputLimitAndCancellationAreEnforced()
    {
        using var huge = new MemoryStream(new byte[LineDiff.MaxInputBytes + 1]);
        await Assert.ThrowsAsync<TextLimitException>(() => LineDiff.ReadAsync(huge));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => LineDiff.Compare("a", "b", cancellationToken: cts.Token));
    }
    [Fact]
    public void GeneratedMutationsReconstructBothSides()
    {
        var random = new Random(174);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            string[] a = Enumerable.Range(0, random.Next(1, 30)).Select(_ => random.Next(8).ToString()).ToArray();
            var b = a.ToList(); for (int i = 0; i < 5; i++) { if (b.Count > 0) b.RemoveAt(random.Next(b.Count)); b.Insert(random.Next(b.Count + 1), "new"); }
            var diff = LineDiff.Compare(string.Join('\n', a), string.Join('\n', b));
            Assert.Equal(a, diff.Lines.Where(e => e.Kind != EditKind.Added).Select(e => e.Text));
            Assert.Equal(b, diff.Lines.Where(e => e.Kind != EditKind.Removed).Select(e => e.Text));
        }
    }
}
