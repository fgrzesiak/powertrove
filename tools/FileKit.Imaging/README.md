# FileKit.Imaging

Header-only image probing for .NET 10: format, width, height and EXIF orientation, without decoding pixels. Part of the FileKit tool family; usable on its own.

Its main purpose is to refuse "pixel flood" images, which are tiny files that declare enormous dimensions, before a decoder allocates memory for them.

Supported: JPEG (SOF markers, EXIF orientation), PNG (IHDR), GIF, BMP (core and info headers, top-down), TIFF (first IFD, orientation), WebP (VP8, VP8L, VP8X), HEIF/HEIC and AVIF (`ispe` property inside `meta/iprp/ipco`).

```csharp
var info = await ImageProbe.ProbeFileAsync(path);
if (info.PixelCount > 120_000_000) throw new InvalidOperationException("Image too large.");
var (width, height) = info.DisplaySize; // orientation applied
```

Reads at most 1 MB. Malformed or truncated headers throw `ImageProbeException`.

## Build

```
dotnet test FileKit.Imaging.slnx
```

License: not yet selected by the owner.


## Raster and output APIs

`RasterImage` stores straight-alpha BGRA bytes. `RasterOperations` supplies crop, rotations, flips, padding, source-over composition, Rec.709 grayscale, pixelation and alpha-correct bilinear resize. Each raster is checked before allocating and is limited to 60 million pixels and 30000 px per side. Long pixel operations accept cancellation tokens.

`PaletteQuantizer` uses deterministic RGBA median cut with a bounded histogram (at most 65536 buckets after reduction) and optional Floyd–Steinberg diffusion. Inputs with no more colors than the requested palette retain exact colors. `PngEncoder` writes 8-bit indexed (PLTE/tRNS), RGB and RGBA PNG, selecting row filters by minimum signed-byte sum and streaming zlib output through 64 KiB IDAT chunks. `IcoWriter` packages up to seven validated PNG entries, each at most 256 px.

`ImageProbe.ProbeMetadataFlags` and its async seekable-stream overload return non-identifying flags, stored DPI and structural GIF/WebP frame counts. The scanner skips pixel payloads, bounds file size to 300 MiB and rejects malformed container lengths. Unsupported facts use nullable fields rather than invented values. The span overload copies its input; prefer the stream overload for files.


`RasterOperations.PixelateRegions` handles several regions with one output allocation, preserves the source and validates the entire plan first. It permits at most 1024 regions and limits summed region work to eight 60 MP rasters. It is used by local face pixelation; detection and UI warnings stay outside FileKit.
