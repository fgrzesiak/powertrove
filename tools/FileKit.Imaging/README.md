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

`ImageCanvasLayout` describes an exact transparent canvas, image-layer dimensions,
center-relative offsets and an integer rotation angle. `ImageCanvasRenderer.Render`
uses one inverse affine transform with alpha-correct bilinear sampling, clips at
the canvas boundary and supports cancellation. Canvas and layer limits are 10,000
pixels per side and 40 million pixels. `ImageCanvasEditorState` provides platform-free
drag/rotation math, aspect locking, fitting, alignment and bounded geometry-only
undo/redo. It has no window, file-system or PowerTrove dependency.

`RasterImage` stores straight-alpha BGRA bytes. `RasterOperations` supplies crop, rotations, flips, padding, source-over composition, Rec.709 grayscale, pixelation and alpha-correct bilinear resize. Each raster is checked before allocating and is limited to 60 million pixels and 30000 px per side. Long pixel operations accept cancellation tokens.

`PaletteQuantizer` uses deterministic RGBA median cut with a bounded histogram (at most 65536 buckets after reduction) and optional Floyd–Steinberg diffusion. Inputs with no more colors than the requested palette retain exact colors. `PngEncoder` writes 8-bit indexed (PLTE/tRNS), RGB and RGBA PNG, selecting row filters by minimum signed-byte sum and streaming zlib output through 64 KiB IDAT chunks. `IcoWriter` packages up to seven validated PNG entries, each at most 256 px.

`ImageProbe.ProbeMetadataFlags` and its async seekable-stream overload return non-identifying flags, stored DPI and structural GIF/WebP frame counts. The scanner skips pixel payloads, bounds file size to 300 MiB and rejects malformed container lengths. Unsupported facts use nullable fields rather than invented values. The span overload copies its input; prefer the stream overload for files.


`RasterOperations.PixelateRegions` handles several regions with one output allocation, preserves the source and validates the entire plan first. It permits at most 1024 regions and limits summed region work to eight 60 MP rasters. It is used by local face pixelation; detection and UI warnings stay outside FileKit.

`SvgRasterSource` validates a deliberately limited static SVG subset before a caller
can pass it to a renderer. It supports shapes, text and local gradients, rejects
DTDs, processing instructions, active content, stylesheets, images, use references,
foreign namespaces and external resources, and fits the output to a requested
bounding box while preserving aspect ratio. The `exactCanvas` overloads keep the
requested output dimensions and center the complete viewBox with proportional
scaling and transparent padding, overriding source stretch/crop alignment.
Limits: 1 MiB input, 5,000 elements,
64 levels, 10,000 pixels per output side and 40 million output pixels. It rewrites
root pixel dimensions after validation and requires a viewBox or pixel dimensions.
It is not a general-purpose SVG browser and does not render by itself.

`RasterVectorizer` traces palette-quantized raster regions into closed SVG polygon
paths with even-odd holes. It reuses `PaletteQuantizer` without dithering, supports
color/black-white and three bounded working resolutions, and embeds no raster.
It is intended for flat artwork; gradients, photos and curves are approximated.
Source, boundary-edge and output limits fail explicitly.

## JPEG coefficient transforms (BCL-only)

`FileKit.Imaging.Jpeg.JpegCoefficientCodec` supports bounded 8-bit baseline JPEG
and progressive Huffman JPEGs through quantized DCT coefficient arrays. It
recognizes restart markers and MCU sampling, decodes spectral/successive scans
without an RGB decoder, transforms 8×8 block positions and frequency signs for
orthogonal rotations and mirroring, and emits JPEG entropy data with the same
quantized DCT coefficients. Subsampling and source APP/COM metadata are retained;
the caller remains responsible for normalizing orientation and dimensions within
EXIF/XMP/JFIF metadata.

Current limits: 300 MiB JPEG input/output, 60 MP, 30,000 pixels per axis, 96
million DCT coefficients, 32 scans, and 1 MiB before the first scan. Transforms
other than identity reject partial MCU edges without cropping. Unsupported
arithmetic coding, lossless JPEG coding and 16-bit quantization are rejected;
quantized coefficients remain exact, but output entropy coding, scan structure
and compressed file size can differ. Progressive inputs are written as baseline
JPEGs. No additional NuGet library or native JPEG executable is used.

**Acceptance before release:** run the original adapter tests plus FileKit
coefficient tests locally on Windows and compare against independently
generated LibJpeg.NET coefficient baselines, including 4:2:0/4:2:2/4:4:4,
progressive scan patterns, Restart intervals, EXIF/XMP/JFIF and malformed files.
The source implementation alone does not demonstrate compatibility.
