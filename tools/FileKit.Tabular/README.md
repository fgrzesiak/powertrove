# FileKit.Tabular

BCL-only .NET 10 tables, bounded UTF-8 RFC 4180 CSV parsing and CSV/JSON/Markdown/SQL serialization. Cell types are Empty, Text, Number (double), Boolean, Date and Error. CSV input remains text, never guessed numbers or formulas. Header mode resolves blanks and duplicate names case-insensitively using the established table-export convention. Ragged rows, invalid quoting, invalid UTF-8 and exceeded limits throw TabularException. Defaults: 100 MB, 1,000,000 records, 16,384 columns and 4,000,000 cells. Delimiter detection counts comma/semicolon/tab outside quoted fields in the first ten records.

Writers preserve the existing PowerTrove table-export conventions: CSV CRLF; Markdown LF; SQL TextWriter newline and dialect quoting; indented JSON with typed values. SQL exports are text, never executed. Formatted display values are not substituted for typed values. Number cells use double; use TextCell to preserve arbitrary precision.

Standalone commands: `dotnet test FileKit.Tabular.slnx`. Tests were added but not executed by the assistant. License: Apache-2.0; see the repository root LICENSE.

`JsonObjectTable.Project` projects nonempty JSON arrays of objects into typed tables.
It unions columns in first-seen order, supports optional dotted-path flattening and
an explicit ordered column selection, and rejects duplicate properties or ambiguous
flattened names. Arrays and unflattened objects remain JSON text. Dates remain text;
booleans, nulls and Excel-safe numbers retain their types. Numbers that exceed
Excel's 15-significant-digit representation remain verbatim text. Limits are one
million data rows, 16,384 columns and four million cells including a header row.
Cancellation is checked during enumeration. The caller owns bounded JSON parsing
and the lifetime of the input `JsonDocument`.

`VCard` handles UTF-8 vCard 3.0/4.0 with Unicode-safe 75-byte line folding, escaping,
multiple phone/email/address fields and common name/organization/title/note fields.
`ToTable` emits canonical English interchange headers and indexed repeated fields;
`FromTable` also recognizes a limited set of common Google/Outlook-style column
spellings. Unknown nonempty import columns fail rather than being dropped. This is
not a claim of complete vendor-export compatibility. vCard 2.1, quoted-printable,
legacy charsets and embedded binary encodings are rejected. Unknown properties are
preserved when merging cards but are not included in common-field table exports.
Limits: 32 MiB, 100,000 contacts, 1,024 properties per contact and four million
spreadsheet cells. The library does not follow URIs, fetch photos or evaluate formulas.

`LayoutTableDetector` consumes typed positioned words/rules and detects complete
rectangular grids or conservative aligned unruled tables. It is independent of
PDF parsing, preserves empty/multiline cells and refuses unclear/merged layouts.
See the implementation record for limits and unsupported structures.
