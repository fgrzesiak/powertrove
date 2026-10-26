using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;
using FileKit.Imaging;

namespace ContextBar.Pack.Images;

public sealed record ImageRaster(int Width, int Height, byte[] Bgra32, double DpiX = 96, double DpiY = 96);

public interface IImageRasterCodec
{
    bool CanDecode(string format);
    bool CanEncode(string format);
    Task<ImageRaster> DecodeAsync(Stream source, string sourceFormat, long maxPixels, CancellationToken ct);
    Task EncodeAsync(ImageRaster raster, Stream destination, string targetFormat, int quality, CancellationToken ct);
}

/// <summary>Uses the preferred raster codec, then the existing engines through a metadata-free PNG bridge.</summary>
public sealed class CompositeRasterCodec(IImageRasterCodec preferred, CompositeImageEngine engines) : IImageRasterCodec
{
    public bool CanDecode(string format) => preferred.CanDecode(format) || engines.CanDecode(format);
    public bool CanEncode(string format) => preferred.CanEncode(format) || engines.CanEncode(format);

    public async Task<ImageRaster> DecodeAsync(Stream source, string sourceFormat, long maxPixels, CancellationToken ct)
    {
        if (preferred.CanDecode(sourceFormat)) return await preferred.DecodeAsync(source, sourceFormat, maxPixels, ct).ConfigureAwait(false);
        if (!source.CanSeek) throw new FileProcessingException(RasterMessages.DecodeFailed);
        long start = source.Position;
        if (source.Length - start > ImageLimits.Default.MaxFileBytes) throw new FileProcessingException(RasterMessages.DecodeFailed);
        ImageInfo info;
        try { info = await ImageProbe.ProbeAsync(source, ct).ConfigureAwait(false); }
        catch (ImageProbeException ex) { throw new FileProcessingException(RasterMessages.DecodeFailed, ex); }
        source.Position = start;
        if (maxPixels < 1 || info.PixelCount > Math.Min(maxPixels, RasterImage.MaxPixels) || info.Width > 30000 || info.Height > 30000)
            throw new FileProcessingException(RasterMessages.DecodeFailed);
        using var png = new MemoryStream();
        await engines.TransformAsync(source, png, new ImageTransformRequest(sourceFormat, FileFormats.Png, 0, null, null, false, true), ct).ConfigureAwait(false);
        png.Position = 0;
        return await preferred.DecodeAsync(png, FileFormats.Png, maxPixels, ct).ConfigureAwait(false);
    }

    public async Task EncodeAsync(ImageRaster raster, Stream destination, string targetFormat, int quality, CancellationToken ct)
    {
        if (preferred.CanEncode(targetFormat))
        {
            await preferred.EncodeAsync(raster, destination, targetFormat, quality, ct).ConfigureAwait(false); return;
        }
        using var png = new MemoryStream();
        await Task.Run(() => PngEncoder.Encode(new RasterImage(raster.Width, raster.Height, raster.Bgra32), png, ct: ct), ct).ConfigureAwait(false);
        png.Position = 0;
        await engines.TransformAsync(png, destination, new ImageTransformRequest(FileFormats.Png, targetFormat, quality, null, null, false, true), ct).ConfigureAwait(false);
    }
}
