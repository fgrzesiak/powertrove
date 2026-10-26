# FileKit.Detection

Dependency-free file format detection from content signatures (magic bytes) for .NET 10. Reads at most 1 KB and never decodes content. Part of the FileKit tool family; usable on its own.

Detects JPEG, PNG, GIF, BMP (with DIB header plausibility check), TIFF, WebP, AVIF, HEIC/HEIF (by ISO brands), ICO, PDF (header within the first 1024 bytes), ZIP, GZip, 7z, RAR, MP3 (ID3 or frame sync), AAC (ADTS), WAV, FLAC, Ogg, ISO base media (MP4/M4A/MOV, with major brand), Matroska/WebM (with doc type), AVI, ASF (WMV/WMA) and Windows executables.

```csharp
var result = await FileSignature.DetectFileAsync(path);
if (result.Format == FileFormat.Pdf) { /* ... */ }
```

Use it to refuse renamed files (for example an executable named `invoice.jpg`) before passing data to a decoder.

## Build

```
dotnet test FileKit.Detection.slnx
```

License: not yet selected by the owner.

TAR detection recognizes `ustar` at offset 257. TAR.GZ is detected as Gzip; decompression/container validation belongs to the archive tool. Magic-byte detection is not an archive validity guarantee.
