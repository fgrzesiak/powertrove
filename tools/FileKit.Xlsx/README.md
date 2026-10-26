# FileKit.Xlsx

Small BCL-only OOXML spreadsheet reader and writer for .NET 10. No Office automation, network, formula evaluation or external packages.

```csharp
using FileKit.Xlsx;
var workbook = new Workbook(new[] {
    new Worksheet("Data", new IReadOnlyList<Cell>[] {
        new Cell[] { new TextCell("Value"), new TextCell("Date") },
        new Cell[] { new NumberCell(1.25), new DateCell(new DateTime(2024, 1, 2)) }
    }, HasHeader: true)
});
await XlsxWriter.WriteAsync(output, workbook, cancellationToken);
var copy = await XlsxReader.ReadAsync(input, cancellationToken: cancellationToken);
```

Inputs must be seekable, positioned at the beginning of a ZIP package. The caller retains ownership of streams. Sheet cells are zero-based, include sparse empty positions and retain empty trailing cells. `Worksheet.HasHeader` controls bold first-row styling and a frozen first row when writing; reading does not infer header semantics. The reader supports shared and inline rich strings, finite numbers, booleans, errors, ISO dates, built-in date formats 14–22, custom calendar formats and the 1904 epoch. Merges expose only their top-left value. Excel's fictitious 1900-02-29 is returned as `ErrorCell("#VALUE!")`. Time-only formats remain numbers. Formula expressions are never interpreted; only cached results are read, and `Workbook.HasFormulas` signals their presence. A missing cached result is empty.

Default guards: 100 MiB per XML part, 256 MiB total/compressed input, ratio 1000, 10,000 ZIP parts, 256 sheets, 1,048,576 rows and 16,384 columns per sheet, four million materialized cells per workbook. Merged-range traversal is bounded too. XML DTDs and resolvers are disabled; duplicate ZIP names, traversal parts and external worksheet relationships are refused. Unsupported or malformed input raises `XlsxException`; cancellation raises `OperationCanceledException`.

The writer emits inline strings (including formula-looking text), typed values, date styles, bold headers and optional frozen headers. Formatting, macros, drawings, comments and formula expressions are intentionally not copied. Use `SheetNames.Unique` for sanitized, truncated, case-insensitively unique names.

The solution contains generated round-trip, malformed-part, relationship mutation and size/ratio guard test sources. Tests have not been executed for this change. Optional Windows acceptance: open a generated workbook in Excel and verify typed values, headers, dates and multiple sheets without repair prompts.
