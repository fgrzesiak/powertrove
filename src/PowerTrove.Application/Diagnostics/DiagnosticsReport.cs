using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PowerTrove.Core.Diagnostics;

namespace PowerTrove.Application.Diagnostics;

public sealed record DiagnosticsInfo(
    string AppVersion,
    string OsVersion,
    string Architecture,
    string Distribution,
    int ActionCount,
    long WorkingSetBytes,
    IReadOnlyList<MetricSummary> Timings);

/// <summary>
/// The text "Copy diagnostics" puts on the clipboard for a bug report. It holds versions, counts and
/// timings only: no file names, paths, selected content or settings values. The labels stay English
/// like log formats, so maintainers can compare reports regardless of the UI language.
/// </summary>
public static class DiagnosticsReport
{
    public static string Build(DiagnosticsInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var text = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;
        text.AppendLine("PowerTrove diagnostics");
        text.AppendLine(invariant, $"Version: {info.AppVersion} ({info.Distribution})");
        text.AppendLine(invariant, $"Windows: {info.OsVersion} {info.Architecture}");
        text.AppendLine(invariant, $"Actions: {info.ActionCount}");
        text.AppendLine(invariant, $"Memory: {info.WorkingSetBytes / (1024 * 1024)} MB working set");
        text.AppendLine("Timings this session (ms): count / median / p95 / max");
        if (info.Timings.Count == 0) text.AppendLine("  none yet, open the palette a few times first");
        foreach (var metric in info.Timings)
            text.AppendLine(invariant, $"  {metric.Name}: {metric.Count} / {metric.P50:0.0} / {metric.P95:0.0} / {metric.Max:0.0}");
        return text.ToString();
    }
}
