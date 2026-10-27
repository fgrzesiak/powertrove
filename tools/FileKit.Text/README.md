# FileKit.Text

`MediaTimeText` parses bounded nonnegative media times (seconds, minutes:seconds
or hours:minutes:seconds, up to seven days) and formats invariant second values.
It can be shared by a parameter form and a visual timeline without a platform or
application dependency.

BCL-only .NET 10 Myers line diff with a self-contained, HTML-encoded report writer.

```csharp
var before = await LineDiff.ReadAsync(firstStream, cancellationToken);
var after = await LineDiff.ReadAsync(secondStream, cancellationToken);
var diff = LineDiff.Compare(before, after, ignoreLineEndings: true, cancellationToken);
await LineDiff.WriteHtmlAsync(writer, diff,
    new ReportLabels("Comparison", "Added", "Removed", "Unchanged"), cancellationToken);
```

`ReadAsync` accepts at most 5 MiB per input, uses strict UTF-8 without a BOM and recognizes Unicode BOMs. `Compare` accepts at most 100,000 lines per side and bounds Myers trace/search work to eight million units. Difficult comparisons raise `TextLimitException` instead of consuming unbounded memory. Changes count as removals plus additions. An empty file has zero lines. Ignoring line endings normalizes CRLF/LF/CR and ignores a terminal newline; exact mode includes each terminator in the compared text.

Reports include only inline CSS, encoded user content and a restrictive content-security policy; no scripts, network resources or filename headers are inserted. Labels are supplied by the caller for localization. Cancellation is supported throughout. Streams and writers remain owned by the caller.

Generated mutation, reconstruction, encoding, cancellation and HTML safety test sources are provided; tests have not been run for this change.

`Subtitles` reads bounded UTF-8 SRT and WebVTT, validates cue timing, writes normalized
CRLF output and applies positive/negative millisecond offsets without mutating the
input document. A shift before zero is rejected instead of dropping/clipping cues.
Limits: 16 MiB, 100,000 cues and timestamps within seven days. WebVTT identifiers
and supported layout settings survive shifts; unsupported header metadata,
STYLE/REGION/NOTE blocks, inline timestamp shifts and lossy cross-format markup or
layout conversions are rejected explicitly. Simple bold/italic/underline markup
can be converted. No renderer, network access or media binary is required.
