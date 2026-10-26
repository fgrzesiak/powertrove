using System.Runtime.InteropServices.WindowsRuntime;
using PowerTrove.Abstractions.Files;
using PowerTrove.Pack.Images;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PowerTrove.Pack.Images.Windows;

/// <summary>WIC raster adapter; limits are checked before requesting pixel data.</summary>
public sealed class WindowsRasterCodec : IImageRasterCodec
{
    private const long PixelLimit = 60_000_000;
    private const int MaxFileBytes = 300 * 1024 * 1024;
    private readonly WindowsImagingEngine _capabilities = new();
    public bool CanDecode(string format) => _capabilities.CanDecode(format);
    public bool CanEncode(string format) => _capabilities.CanEncode(format);

    public async Task<ImageRaster> DecodeAsync(Stream source, string sourceFormat, long maxPixels, CancellationToken ct)
    {
        try
        {
            if (!CanDecode(sourceFormat) || maxPixels < 1) throw new InvalidDataException();
            using var input = new InMemoryRandomAccessStream();
            var buffer = new byte[65536]; int total = 0, read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                total = checked(total + read); if (total > MaxFileBytes) throw new InvalidDataException();
                await input.WriteAsync(buffer.AsBuffer(0, read)).AsTask(ct).ConfigureAwait(false);
            }
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(ct).ConfigureAwait(false);
            if ((long)decoder.PixelWidth * decoder.PixelHeight > Math.Min(maxPixels, PixelLimit) || decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || decoder.PixelWidth > 30000 || decoder.PixelHeight > 30000)
                throw new InvalidDataException();
            SoftwareBitmap bitmap;
            try
            {
                bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                    new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // WIC cannot color-manage indexed (palette) images; they carry no color profile anyway.
                bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                    new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);
            }
            using var ownedBitmap = bitmap;
            var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)]; bitmap.CopyToBuffer(pixels.AsBuffer());
            return new ImageRaster(bitmap.PixelWidth, bitmap.PixelHeight, pixels, Dpi(decoder.DpiX), Dpi(decoder.DpiY));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileProcessingException(RasterMessages.DecodeFailed, ex);
        }
    }

    public async Task EncodeAsync(ImageRaster raster, Stream destination, string targetFormat, int quality, CancellationToken ct)
    {
        try
        {
            if (raster.Width < 1 || raster.Height < 1 || raster.Width > 30000 || raster.Height > 30000 || (long)raster.Width * raster.Height > PixelLimit ||
                raster.Bgra32 is null || raster.Bgra32.Length != (long)raster.Width * raster.Height * 4) throw new InvalidDataException();
            ct.ThrowIfCancellationRequested();
            byte[] pixels = raster.Bgra32;
            bool opaqueOutput = targetFormat is FileFormats.Jpeg or FileFormats.Bmp;
            if (opaqueOutput)
            {
                pixels = (byte[])pixels.Clone();
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if ((i & 65535) == 0) ct.ThrowIfCancellationRequested();
                    int alpha = pixels[i + 3];
                    for (int c = 0; c < 3; c++) pixels[i + c] = (byte)((pixels[i + c] * alpha + 255 * (255 - alpha) + 127) / 255);
                    pixels[i + 3] = 255;
                }
            }
            bool gray = targetFormat == FileFormats.Jpeg;
            if (gray) for (int i = 0; i < pixels.Length; i += 4)
            {
                if ((i & 65535) == 0) ct.ThrowIfCancellationRequested();
                if (pixels[i] != pixels[i + 1] || pixels[i] != pixels[i + 2]) { gray = false; break; }
            }
            var format = BitmapPixelFormat.Bgra8;
            if (gray)
            {
                byte[] luminance = new byte[raster.Width * raster.Height];
                for (int i = 0; i < luminance.Length; i++) luminance[i] = pixels[i * 4];
                pixels = luminance; format = BitmapPixelFormat.Gray8;
            }
            using var output = new InMemoryRandomAccessStream();
            var properties = new BitmapPropertySet();
            if (targetFormat == FileFormats.Jpeg) properties.Add("ImageQuality", new BitmapTypedValue(Math.Clamp(quality, 1, 100) / 100f, global::Windows.Foundation.PropertyType.Single));
            var id = targetFormat switch
            {
                FileFormats.Jpeg => BitmapEncoder.JpegEncoderId, FileFormats.Png => BitmapEncoder.PngEncoderId,
                FileFormats.Bmp => BitmapEncoder.BmpEncoderId, FileFormats.Tiff => BitmapEncoder.TiffEncoderId,
                FileFormats.Gif => BitmapEncoder.GifEncoderId, _ => throw new InvalidDataException()
            };
            var encoder = await BitmapEncoder.CreateAsync(id, output, properties).AsTask(ct).ConfigureAwait(false);
            try
            {
                encoder.SetPixelData(format, gray || opaqueOutput ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight,
                    (uint)raster.Width, (uint)raster.Height, Dpi(raster.DpiX), Dpi(raster.DpiY), pixels);
                await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
            }
            catch (ArgumentException) when (gray)
            {
                // The WinRT JPEG encoder rejects Gray8 input; encode the same gray pixels as BGRA.
                output.Size = 0;
                encoder = await BitmapEncoder.CreateAsync(id, output, properties).AsTask(ct).ConfigureAwait(false);
                byte[] bgra = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++) { bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = pixels[i]; bgra[i * 4 + 3] = 255; }
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    (uint)raster.Width, (uint)raster.Height, Dpi(raster.DpiX), Dpi(raster.DpiY), bgra);
                await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
            }
            output.Seek(0); using var encoded = output.AsStreamForRead(); await encoded.CopyToAsync(destination, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileProcessingException(RasterMessages.EncodeFailed, ex);
        }
    }

    private static double Dpi(double dpi) => double.IsFinite(dpi) && dpi > 0 ? dpi : 96;
}
