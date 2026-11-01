using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

namespace PowerTrove.Core.Diagnostics;

public sealed record MetricSummary(string Name, int Count, double P50, double P95, double Max);

/// <summary>
/// Keeps the latest timings of this session in memory so a user can copy them into a bug report.
/// Nothing is written to disk or sent anywhere. Only durations and provider ids are kept, never
/// selection content, file names or paths.
/// </summary>
public sealed class PerformanceJournal : IDisposable
{
    public const int Capacity = 256;

    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<double>> _samples = new(StringComparer.Ordinal);
    private readonly MeterListener? _listener;

    /// <summary>A journal that only receives explicit <see cref="Record"/> calls.</summary>
    public PerformanceJournal() { }

    private PerformanceJournal(string meterName)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meterName) listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            string? provider = null;
            foreach (var tag in tags)
                if (tag.Key == InvocationMetrics.ProviderTag) provider = tag.Value as string;
            Record(provider is null ? instrument.Name : instrument.Name + " / " + provider, value);
        });
        _listener.Start();
    }

    /// <summary>Listens to PowerTrove's invocation metrics for the lifetime of the journal.</summary>
    public static PerformanceJournal Listen() => new(InvocationMetrics.MeterName);

    public void Record(string name, double milliseconds)
    {
        if (string.IsNullOrEmpty(name) || double.IsNaN(milliseconds) || milliseconds < 0) return;
        lock (_lock)
        {
            if (!_samples.TryGetValue(name, out var queue))
                _samples[name] = queue = new Queue<double>(Capacity);
            if (queue.Count == Capacity) queue.Dequeue();
            queue.Enqueue(milliseconds);
        }
    }

    public IReadOnlyList<MetricSummary> Summaries()
    {
        lock (_lock)
        {
            return _samples
                .Where(entry => entry.Value.Count > 0)
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry =>
                {
                    var sorted = entry.Value.OrderBy(v => v).ToArray();
                    return new MetricSummary(entry.Key, sorted.Length, Percentile(sorted, 0.50), Percentile(sorted, 0.95), sorted[^1]);
                })
                .ToArray();
        }
    }

    /// <summary>Nearest-rank percentile of an ascending, non-empty array.</summary>
    public static double Percentile(IReadOnlyList<double> ascending, double fraction)
    {
        ArgumentNullException.ThrowIfNull(ascending);
        if (ascending.Count == 0) throw new ArgumentException("At least one sample is required.", nameof(ascending));
        int rank = (int)Math.Ceiling(Math.Clamp(fraction, 0, 1) * ascending.Count);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Count - 1)];
    }

    public void Dispose() => _listener?.Dispose();
}
