using FileKit.Imaging;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;

namespace PowerTrove.Pack.Images;

/// <summary>In-memory, bounded previews. No output writer, temporary files or shared pixel cache.</summary>
internal static class ImagePreviews
{
    public static async Task<ImageRaster> DecodeAsync(FileActionServices services, IImageRasterCodec codec, SelectedFile file, CancellationToken ct)
    {
        var info = await RasterSupport.ProbeAsync(services, file, ct).ConfigureAwait(false);
        string format = info.Format switch {
            ImageFormat.Png => FileFormats.Png, ImageFormat.Jpeg => FileFormats.Jpeg, ImageFormat.Gif => FileFormats.Gif,
            ImageFormat.Bmp => FileFormats.Bmp, ImageFormat.Tiff => FileFormats.Tiff, ImageFormat.WebP => FileFormats.WebP,
            ImageFormat.Heif => FileFormats.Heic, ImageFormat.Avif => FileFormats.Avif,
            _ => throw new FileProcessingException(RasterMessages.DecodeFailed) };
        if (!codec.CanDecode(format)) throw new FileProcessingException(RasterMessages.DecodeFailed);
        await using var stream = services.FileSystem.OpenRead(file.Path!);
        var decoded = await codec.DecodeAsync(stream, format, RasterImage.MaxPixels, ct).ConfigureAwait(false);
        _ = RasterSupport.Pixels(decoded);
        return decoded;
    }

    public static RasterImage Thumbnail(RasterImage source, CancellationToken ct)
    {
        double scale = Math.Min(1, 512.0 / Math.Max(source.Width, source.Height));
        return RasterOperations.ResizeBilinear(source, Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)), ct);
    }

    public static RasterImage Expand(IndexedRaster indexed, CancellationToken ct)
    {
        var bytes = new byte[RasterImage.ByteLength(indexed.Width, indexed.Height)];
        for (int y = 0; y < indexed.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < indexed.Width; x++)
            {
                int p = y * indexed.Width + x, i = p * 4; var c = indexed.Palette[indexed.Indices[p]];
                bytes[i] = c.B; bytes[i + 1] = c.G; bytes[i + 2] = c.R; bytes[i + 3] = c.A;
            }
        }
        return new(indexed.Width, indexed.Height, bytes);
    }

    public static ImagePreviewData Frame(RasterImage pixels, CancellationToken ct, int? width = null, int? height = null)
    {
        var thumbnail = Thumbnail(pixels, ct);
        using var png = new MemoryStream(); PngEncoder.Encode(thumbnail, png, ct: ct);
        return new(width ?? pixels.Width, height ?? pixels.Height, thumbnail.Width, thumbnail.Height, Convert.ToBase64String(png.ToArray()));
    }
}
