# Changelog

All notable changes to FileKit.Pdf are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

- Added production PDF AES-256 R6 writing with random per-document key, user/owner salts, IVs and permission flags. Encrypted compact mode is rejected.
- Added appearance-based flattening of PDF fields/comments, configurable hyperlink preservation and preview counts for discarded annotations.


- Added `PdfPngWriter.Write` for bounded 8-bit Gray/RGB PNG output with CRC-checked chunks, and `PdfImageExtractor.TryExtract` for byte-preserving JPEG / decoded Flate image extraction. Unsupported masks, sample transforms and filter chains return no image; malformed samples and safety-limit violations throw typed PDF errors.
- `PdfImages.Enumerate` now accepts an optional cancellation token and checks it while walking page and form resources.
- Added extraction, independent PNG pixel/CRC, predictor, unsupported-feature, malformed-data, mutation and cancellation regression sources.

- Fixed compact deduplication emitting invalid reference-only object bodies. Copied streams and dictionaries are now processed after their dependencies, all references are remapped, and duplicate numbers are omitted and marked free in the cross-reference stream. Hash matches are verified byte for byte; page-tree identity and cyclic candidates are retained. The classic writer is unchanged.
- Replaced the page-count-only deduplication check with structural, font-sharing, cascading-remap, near-match, cycle, multi-batch and deterministic-output regression coverage, plus all-page pdfTeX content checks and a fixed-ID literal classic-output baseline.

- Added `PdfTools.Repair` to write a full structurally sound copy of a damaged PDF (preserving all metadata and features) while using the compact object stream writer.
- `PdfWriteOptions` now supports `EditMetadata` to inject or clear `Title`, `Author`, `Subject`, and `Keywords`.
- Added `PdfTools.EditMetadata` to update standard PDF properties.
- `PdfTextDecoration` now supports `Invisible` mode for rendering text as `3 Tr` and `HorizontalScaling` (`Tz` operator parameter) to scale text perfectly.
- `PdfTextMeasurement` is now public to allow clients to calculate text metrics dynamically for advanced formatting and spacing layouts.
- Writer: `PdfTools.Merge` now supports `fileBookmarkTitles` to create nested outlines per file.
- Writer: added `PdfDocumentBuilder.SetOutline(IReadOnlyList<PdfOutlineItem>)` to write the document outline.
- Outline items are now mapped and kept in extract, remove, reorder, rotate, and split operations.
- Added `PdfOutline.Read` to read PDF outlines (bookmarks) into a hierarchical structure.
- Fixed `PdfImages.Enumerate` failing to resolve images inside inherited `/Pages` resources.
- Fixed `PdfImages.TryDecodeFlatePixels` failing to read predictors when `/DecodeParms` is an array.
- Mitigated massive memory allocation vulnerability in PNG predictor by properly enforcing `PdfReadLimits`.
- Fixed indirect dictionary parsing for decorations
- Fixed indirect contents array append behavior
- Fixed MediaBox/CropBox resolution
- Fixed WinAnsi string encoding for decorations

### Added
- Standard Security Handler decryption (revisions 2-6) supporting RC4, AES-128, and AES-256.
- `PdfPasswordRequiredException` for password-protected documents.
- P12: Added `PdfTools.StampImage` to add images (including alpha-channel PNGs) or visual signatures to selected PDF pages while keeping the XObject shared.

### Added
- Added `PdfDocumentBuilder.AddComposedPage` to wrap PDF pages inside Form XObjects with applied cm transformations and mapped annotations.
- Added `PdfTools.Crop` to non-destructively crop margins by inset bounds inside the MediaBox.
- Added `PdfTools.ResizePages` to resize and center pages onto standard formats (A4, Letter, A5, Legal) with proportional fitting.


### Added
- P8: Added page decoration infrastructure (`PdfDecoration`, `PdfTextDecoration`, `PdfImageDecoration`, `PdfDocumentBuilder.AddPage` with decorations) for future text/image watermarks and stamps.
- P11: Added `PdfTools.AddWatermark` to stamp text watermarks onto PDF pages with configurable styles (diagonal, horizontal, tiled).


### Added

- Added `PdfTools.Interleave` to combine two documents by alternately taking pages from each.
- Added `PdfTools.Insert` to insert one document into another at a specified page index.
- Added `PdfTools.Reorder` to reorder the pages of a PDF document given a valid permutation.
- Added `PdfDocumentBuilder.AddBlankPage` to insert an empty page with given dimensions.
- Added `PdfImages` with `Enumerate`, `GetEncodedData`, and `TryDecodeFlatePixels` to inspect and decode images within a PDF document.
- Added `PdfDocumentBuilder.ReplaceImage` to replace image XObjects while retaining identical aspect ratios and mask properties.
- Added `PdfWriteOptions.Compact` to support structure compaction.

## [0.1.0] - 2026-10-14

### Added

- Initial release, developed inside the PowerTrove repository (see its ADR 0007).
