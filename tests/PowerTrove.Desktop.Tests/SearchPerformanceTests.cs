using System.Diagnostics;
using PowerTrove.Core.Diagnostics;
using PowerTrove.Core.Search;
using Xunit.Abstractions;

namespace PowerTrove.Desktop.Tests;

/// <summary>
/// Search latency with the full built-in catalog and production file verification, measured on every
/// Windows test run. The budget is the engineering target in docs/implementation/testing-strategy.md.
/// A failure means a change made search clearly slower; the output lists the measured percentiles.
/// </summary>
public sealed class SearchPerformanceTests(ITestOutputHelper output) : IDisposable
{
    private const double BudgetMilliseconds = 30;
    private const int Warmup = 3;
    private const int Iterations = 20;
    private readonly SelectionFixtures _fixtures = new();

    [Fact]
    public async Task Search_FullCatalog_StaysWithinTheLatencyBudget()
    {
        var engine = new ActionSearchEngine(_fixtures.Registry);
        var selections = new[]
        {
            _fixtures.Explorer("a.jpg"),
            _fixtures.Explorer("a.jpg", "b.jpg", "c.jpg", "d.png"),
            _fixtures.Explorer("a.pdf", "b.pdf"),
            _fixtures.Explorer("a.jpg", "b.pdf", "c.mp4", "d.csv")
        };
        string[] queries = ["", "comp", "pdf", "convert to webp", "zzzz"];
        var samples = new List<double>();
        foreach (var selection in selections)
            foreach (var query in queries)
            {
                for (int i = 0; i < Warmup; i++) await engine.SearchAsync(query, selection);
                for (int i = 0; i < Iterations; i++)
                {
                    long start = Stopwatch.GetTimestamp();
                    await engine.SearchAsync(query, selection);
                    samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
            }

        samples.Sort();
        double p50 = PerformanceJournal.Percentile(samples, 0.50), p95 = PerformanceJournal.Percentile(samples, 0.95);
        output.WriteLine($"{_fixtures.Registry.GetAllActions().Count()} actions, {samples.Count} searches: median {p50:0.00} ms, p95 {p95:0.00} ms, max {samples[^1]:0.00} ms");
        Assert.True(p95 < BudgetMilliseconds, $"Search p95 {p95:0.00} ms exceeds the {BudgetMilliseconds} ms budget (median {p50:0.00} ms).");
    }

    public void Dispose() => _fixtures.Dispose();
}
