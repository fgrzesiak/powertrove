using System;
using System.Diagnostics.Metrics;

namespace PowerTrove.Core.Diagnostics;

public static class InvocationMetrics
{
    private static readonly Meter s_meter = new("PowerTrove.Core.Invocations", "1.0.0");

    public static readonly Histogram<double> InvocationLatency = s_meter.CreateHistogram<double>(
        name: "invocation.latency",
        unit: "ms",
        description: "The time taken to complete an end-to-end invocation capture and resolution.");

    public static readonly Histogram<double> SearchLatency = s_meter.CreateHistogram<double>(
        name: "search.latency",
        unit: "ms",
        description: "The time taken to match and rank all available actions for a given context.");
}
