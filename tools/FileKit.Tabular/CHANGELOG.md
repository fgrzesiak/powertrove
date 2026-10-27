# Changelog

## [Unreleased]

- Extract established typed table formatting and header resolution.
- Add bounded strict CSV parsing and reusable table writers.
- Expose ordered JSON field discovery without materializing cell values, for
  interactive column selection with the same ambiguity and cancellation rules.

- Add bounded positioned table detection for complete grids and conservative
  unruled tables, with explicit ambiguity failures and reading order.
