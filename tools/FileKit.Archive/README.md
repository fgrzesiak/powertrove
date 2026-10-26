# FileKit.Archive

Safe ZIP creation and extraction for .NET 10, built on `System.IO.Compression`, with no other dependencies. Part of the FileKit tool family; usable on its own.

## Extraction (`SafeZipExtractor`)

Treats every archive as untrusted:

- entry names are normalized and confined to the destination; it rejects absolute paths, drive letters, `..`, reserved device names (`CON`, `NUL`, …), alternate data streams (`:`), trailing dots/spaces and control characters;
- symbolic links (Unix mode in external attributes) and encrypted entries are refused;
- limits on entry count, per-entry and total uncompressed size, path depth and length, and compression ratio (zip bomb guard) are enforced on the bytes actually written, not on declared sizes;
- existing files are never overwritten (`FileMode.CreateNew`), and only files created by the extractor are cleaned up on failure.

Typed errors: `InvalidArchiveException`, `ArchiveLimitExceededException`, `UnsafeArchiveEntryException` (all `ArchiveException`). Extract into a fresh, empty directory and move it into place afterwards; PowerTrove does this transactionally.

```csharp
await SafeZipExtractor.ExtractAsync("download.zip", emptyDirectory,
    new ZipExtractionLimits { MaxTotalUncompressedBytes = 1L << 30 });
```

## Creation (`ZipBuilder`)

Adds files and folders, keeps relative folder structure and empty folders, disambiguates duplicate top-level names (`name (2).ext`), skips links and junctions by default, clamps timestamps to the ZIP range, and enforces entry, size and depth limits.

```csharp
await using var output = File.Create("backup.zip");
await ZipBuilder.CreateAsync(output, new[] { @"C:\data\folder", @"C:\data\file.txt" });
```

## Build

```
dotnet test FileKit.Archive.slnx
```

License: not yet selected by the owner.

## TAR and GZip additions

SafeTarExtractor uses TarReader with a bounded header/PAX guard and shared ArchivePathRules. Only regular files and directories are accepted; unsafe PAX paths, sparse/link/device entries and global headers are refused. TAR.GZ extraction validates compressed output limits and zero-only trailing TAR padding. Callers should use a fresh temporary destination: earlier extracted files remain if a later entry fails. TarBuilder writes PAX TAR.GZ with the ZIP builder's bounded source plan, skips links and keeps empty directories. GZipFile.DecompressAsync handles single streams with actual-byte and ratio limits; compressed inputs require a known length. Extraction rejects existing reparse points below the destination, but callers must still isolate the destination from concurrent untrusted filesystem modification.

## Add to a fresh ZIP

`ZipAppender.AddAsync(sourceArchive, output, sources, limits, cancellationToken)` copies validated existing entries and adds files/folders to a new archive. Input must be seekable. It never uses ZIP update mode. Names are validated with the extraction path rules; encrypted, linked/device, conflicting and oversized entries are rejected. Counts, decompressed sizes and compression ratios use the extraction limits and actual copied bytes. New top-level collisions receive ` (2)`, ` (3)` suffixes, retaining folder subtrees. Linked additions are refused. The output stream must be transactional: discard it on any exception or cancellation. The caller owns both streams and must supply distinct source and destination files.
