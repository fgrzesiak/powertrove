using System;
using System.Collections.Generic;
using System.Linq;
using PowerTrove.Core.Diagnostics;
using Xunit;

namespace PowerTrove.Core.Tests.Diagnostics;

public class PerformanceJournalTests
{
    [Fact]
    public void Summaries_ReportCountMedianP95AndMax()
    {
        using var journal = new PerformanceJournal();
        for (int i = 1; i <= 100; i++) journal.Record("search.latency", i);

        var summary = Assert.Single(journal.Summaries());

        Assert.Equal(new MetricSummary("search.latency", 100, 50, 95, 100), summary);
    }

    [Fact]
    public void Record_KeepsOnlyTheNewestSamples()
    {
        using var journal = new PerformanceJournal();
        for (int i = 0; i < PerformanceJournal.Capacity + 10; i++) journal.Record("palette.shown", i < 10 ? 10_000 : 1);

        var summary = Assert.Single(journal.Summaries());

        Assert.Equal(PerformanceJournal.Capacity, summary.Count);
        Assert.Equal(1, summary.Max);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    public void Record_IgnoresInvalidDurations(double value)
    {
        using var journal = new PerformanceJournal();
        journal.Record("search.latency", value);

        Assert.Empty(journal.Summaries());
    }

    [Theory]
    [InlineData(0.5, 2)]
    [InlineData(0.95, 4)]
    [InlineData(1.0, 4)]
    [InlineData(0.0, 1)]
    public void Percentile_UsesNearestRank(double fraction, double expected)
    {
        Assert.Equal(expected, PerformanceJournal.Percentile(new double[] { 1, 2, 3, 4 }, fraction));
    }

    [Fact]
    public void Listen_ReceivesInvocationMetricsTaggedWithTheProvider()
    {
        using var journal = PerformanceJournal.Listen();
        string provider = "test.provider." + Guid.NewGuid().ToString("N");

        InvocationMetrics.ContextResolveLatency.Record(12.5, new KeyValuePair<string, object?>(InvocationMetrics.ProviderTag, provider));

        var summary = Assert.Single(journal.Summaries(), s => s.Name == "context.resolve / " + provider);
        Assert.Equal(1, summary.Count);
        Assert.Equal(12.5, summary.Max);
    }
}
