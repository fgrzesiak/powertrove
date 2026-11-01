using System;
using System.Diagnostics.Metrics;

namespace PowerTrove.Core.Diagnostics;

public static class InvocationMetrics
{
    public const string MeterName = "PowerTrove.Core.Invocations";

    private static readonly Meter s_meter = new(MeterName, "1.0.0");

    public static readonly Histogram<double> InvocationLatency = s_meter.CreateHistogram<double>(
        name: "invocation.latency",
        unit: "ms",
        description: "The time taken to complete an end-to-end invocation capture and resolution.");

    public static readonly Histogram<double> SearchLatency = s_meter.CreateHistogram<double>(
        name: "search.latency",
        unit: "ms",
        description: "The time taken to match and rank all available actions for a given context.");

    public static readonly Histogram<double> PaletteShownLatency = s_meter.CreateHistogram<double>(
        name: "palette.shown",
        unit: "ms",
        description: "The time from the global shortcut until the palette has rendered.");

    public static readonly Histogram<double> ContextResolveLatency = s_meter.CreateHistogram<double>(
        name: "context.resolve",
        unit: "ms",
        description: "The time a context provider needed to describe the selection; tagged with the provider id.");

    /// <summary>Tag that names the context provider; provider ids never contain selection data.</summary>
    public const string ProviderTag = "provider";
}
