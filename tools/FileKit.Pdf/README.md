# FileKit.Pdf

Managed, dependency-free PDF page operations for .NET 10: merge, split, extract, remove and rotate pages, edit or remove metadata, and build PDFs from JPEG/PNG images. Part of the FileKit tool family; it has no dependency on PowerTrove and can be used on its own.

## Features

- **Reader:** PDF 1.0–2.0 syntax, classic and stream cross-reference sections, object streams, incremental updates and hybrid files. Damaged cross-references are rebuilt by scanning (`WasRepaired`).
- **Writer:** copies pages with every object they reference, renumbers objects and writes a classic cross-reference table. References to pages that are not copied become `null`, so links never pull in excluded pages. Inherited attributes (`Resources`, `MediaBox`, `CropBox`, `Rotate`) are materialized. `AddBlankPage` inserts empty pages.
- **Images:** JPEG is embedded unchanged (DCTDecode, no quality loss, EXIF orientation applied by the page transform). PNG without transparency is embedded with its original compression; PNG with alpha or palette transparency gets a soft mask.
- **Embedded-image extraction:** `PdfImageExtractor.TryExtract(document, image)` returns original JPEG bytes or a newly encoded Gray/RGB PNG, or `null` for unsupported image features. `PdfPngWriter.Write` writes 8-bit PNGs with checked CRCs and bounded row compression. `PdfImages.Enumerate` accepts cancellation and identifies shared image objects once.
- **Decorations:** Invisible (`3 Tr`) and precise width-scaled text bounding box placement is supported via `PdfTextDecoration` configurations.
- **Safety:** `PdfReadLimits` (file size, objects, nesting, decoded stream size, pages, cross-reference sections) and `PdfImageLimits`. Encrypted documents that require a password throw `PdfPasswordRequiredException`. All expected failures derive from `PdfException`, and their messages never contain document content.

## Usage

```csharp
using FileKit.Pdf;

var a = PdfDocument.Open("a.pdf");
var b = PdfDocument.Open("b.pdf");
using (var output = File.Create("merged.pdf"))
    PdfTools.Merge(new[] { a, b }, output);

var doc = PdfDocument.Open("report.pdf");
using (var output = File.Create("selection.pdf"))
    PdfTools.ExtractPages(doc, PageRange.Parse("3, 1-2", doc.PageCount), output);

using (var output = File.Create("photos.pdf"))
    PdfTools.ImagesToPdf(new[] { PdfImage.Load("1.jpg"), PdfImage.Load("2.png") }, output, new PdfImagePageLayout(PdfPageSize.A4));
```

For custom documents, use `PdfDocumentBuilder` (`AddPage`, `AddImagePage`, `Save`).

## Command line

```
dotnet run --project cli/FileKit.Pdf.Cli -- info file.pdf
dotnet run --project cli/FileKit.Pdf.Cli -- merge out.pdf a.pdf b.pdf
dotnet run --project cli/FileKit.Pdf.Cli -- extract in.pdf "1-3, 5" out.pdf
dotnet run --project cli/FileKit.Pdf.Cli -- split in.pdf pages-folder
```

The CLI never overwrites existing files.

## Limitations

- Password-protected PDFs can be read with the supported Standard Security Handler. New encrypted copies use AES-256 R6 via PdfWriteOptions.Encryption; encrypted compact mode is not yet supported. PDF printing/copying permissions are not DRM.
- Flattening copies existing annotation appearances into page content; appearance-less annotations are discarded with warnings. Existing digital signatures can be invalidated.
- Document-level structures are not carried over when pages are copied: named destinations, AcroForm field definitions, the structure tree (tags) and document JavaScript. Outlines (bookmarks) are kept in extract, remove, reorder, rotate, split, and merge. Optional content (layers) of the first source document is kept.
- The default writer uses a classic cross-reference table without object streams, so rewritten files can be slightly larger than their sources. The compact writer (`PdfWriteOptions.Compact = true`) uses object streams and deduplicates copied streams and dictionaries after their child references are final. References to duplicates are remapped to the first canonical object in deterministic dependency order; removed numbers are free cross-reference entries, never reference-only object bodies. SHA-256 candidates are also compared byte for byte.
- Compact deduplication preserves page and page-tree identity. Cyclic candidates and objects depending on those cycles remain distinct; their references to deduplicated leaves are still remapped. Deduplication is based on serialized bytes, so differently ordered dictionaries or differently encoded streams need not merge even if they are semantically equivalent. These limits favor preserving document behavior over smaller output.
- Full document repair via `PdfTools.Repair` correctly preserves all document-level elements (metadata, forms, bookmarks).
- No text extraction, rendering, compression or form filling.
- Embedded-image extraction supports one DCT/Flate filter (including one-element filter arrays) and 8-bit Gray/RGB Flate samples with no predictor or PNG row predictors 10–15. Masks, custom `/Decode`, inline images, TIFF predictors, other color spaces and filter chains are skipped. It does not reproduce page placement, clipping, rotation or transparency. PNG pixels are limited to 64 MiB and each side to 30,000 px; JPEGs retain their encoded-byte/pixel limits. Callers own destination naming, output budgets and atomic commits.

## Verification

`tests/` contains unit tests, real-world fixtures (pdfTeX output with object streams) and mutation tests that only accept typed `PdfException` failures. During development, rewriting 195 locally available third-party PDFs was verified by rendering source and output pages with the Windows PDF engine (identical page counts and pixel-equal renders). The PDFs themselves are not part of the repository.

`PdfCompactDeduplicationTests` covers reference bodies, shared synthetic font programs across independent sources, multi-level and stream-dictionary remapping, one-byte differences, cycles, object-stream batch boundaries, deterministic writes, compact-output mutations, all-page decoded content of the committed pdfTeX fixture, and a literal classic-output byte baseline. Those tests are regression sources; see [alpha acceptance](../../docs/release/alpha.md) for the checks still to run.

## Build

```
dotnet test FileKit.Pdf.slnx
```

Self-contained: `Directory.Build.props` in this folder holds all settings. License: not yet selected by the owner.

## Positioned page interpretation

`PdfLayoutReader.Read(document, options, cancellationToken)` yields positioned glyphs, words, lines and blocks in reading order, axis-aligned stroked lines and rectangles, and placed images. It interprets text, graphics-state, path and XObject operators (including Form XObjects and the parts of a split content array as one stream) and skips inline image data. No external packages, network or file resources are used, and pages are never rasterized: this is not a graphics renderer.

**Text.** Characters map to Unicode through ToUnicode CMaps first, then Differences, the font program's built-in encoding, the Standard/WinAnsi/MacRoman/MacExpert encodings with the Adobe Glyph List, and the embedded TrueType/OpenType `cmap`, CFF charset/encoding or Type1 encoding. Type0 fonts use their encoding CMap's codespace ranges and CID mappings, so one-byte and mixed-length codes decode correctly. A character nothing maps is counted in `SkippedText` and never guessed. Font sizes are the drawn sizes, including the text matrix.

**Words, lines and blocks.** Words follow content order and end at a shown space that leaves a real gap, at a gap of 15% of the font size, or where the text jumps. Lines are rows of words split only at gutters: gaps wider than a font size that the neighbouring rows leave free as well, so justified lines stay whole. Blocks join lines that follow closely below with an overlapping extent and a similar size. Blocks are ordered by recursive XY cuts; consecutive bands that share a gutter form one column area, so columns are read one after the other.

**Tagged PDFs.** Words, glyphs and images carry the marked-content identifier (`Mcid`) they are drawn in and whether they are an `Artifact`; a word never spans two marked-content sequences. `PdfStructure.Read` returns the structure tree with role-mapped standard types, each element's own content as (page, MCID) pairs, its alternative text and table spans, bounded in depth and size and safe against cycles.

**Links and destinations.** Each page lists its link annotations with their rectangle and either an `http`, `https` or `mailto` address (other schemes are dropped) or a `PdfDestination` (page index and view top), resolved from explicit destinations, GoTo actions and named destinations in `/Dests` or the bounded `/Names` tree. `PdfOutline` items carry the same top.

**Images.** JPEG bytes are kept as they are (a soft mask is then dropped). Flate/LZW/ASCII-encoded Gray, RGB, CMYK (also ICCBased or calibrated) and Indexed images with 1 to 16 bits become PNG, RGB or with a Gray soft mask as alpha (scaled when its size differs). JPEG 2000, JBIG2, CCITT, TIFF predictors and other Decode mappings are counted in `SkippedImages`.

The caller sets page, stream, total decode, letter, word, path, image and form-depth limits; `IgnoreExcessPaths` stops collecting rules on a page past the path limit instead of failing. Cancellation is checked at page, operator, glyph, image-row and filter boundaries.

`docs/testing/pdf-layout-parity-corpus.md` describes an optional comparison against a pinned PdfPig baseline on local papers.
