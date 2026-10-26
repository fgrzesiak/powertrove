# FileKit.Tabular

BCL-only .NET 10 tables, bounded UTF-8 RFC 4180 CSV parsing and CSV/JSON/Markdown/SQL serialization. Cell types are Empty, Text, Number (double), Boolean, Date and Error. CSV input remains text, never guessed numbers or formulas. Header mode resolves blanks and duplicate names case-insensitively using the established table-export convention. Ragged rows, invalid quoting, invalid UTF-8 and exceeded limits throw TabularException. Defaults: 100 MB, 1,000,000 records, 16,384 columns and 4,000,000 cells. Delimiter detection counts comma/semicolon/tab outside quoted fields in the first ten records.

Writers preserve the existing ContextBar table-export conventions: CSV CRLF; Markdown LF; SQL TextWriter newline and dialect quoting; indented JSON with typed values. SQL exports are text, never executed. Formatted display values are not substituted for typed values. Number cells use double; use TextCell to preserve arbitrary precision.

Standalone commands: `dotnet test FileKit.Tabular.slnx`. Tests were added but not executed by the assistant. License: Apache-2.0; see the repository root LICENSE.
