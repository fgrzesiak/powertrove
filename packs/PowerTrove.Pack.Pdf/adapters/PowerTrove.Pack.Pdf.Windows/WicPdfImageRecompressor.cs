using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using PowerTrove.Pack.Pdf;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf.Windows;

public sealed class WicPdfImageRecompressor : IPdfImageRecompressor
{
    // Bound native decoding as well as managed buffers before requesting any pixels.
    private const long MaxPixels = 60_000_000;
    private const int MaxDimension = 30_000;

    private static bool FitsPixelBudget(long width, long height) =>
        width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension &&
        checked(width * height) <= MaxPixels && checked(width * height * 4) <= int.MaxValue;

    public async Task<PdfImageReplacement?> RecompressAsync(byte[] encoded, string? filter, PdfImageInfo info, int maxWidth, int maxHeight, int quality, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) return null;
        if (maxWidth <= 0 || maxHeight <= 0) return null;

        if (info.ColorSpace is "DeviceCMYK" or "Indexed" or "ICCBased") return null;
        if (info.HasSoftMask) return null;
        if (filter != "DCTDecode" && filter != "FlateDecode") return null;

        SoftwareBitmap? bitmap = null;

        if (filter == "DCTDecode")
        {
            using var source = new InMemoryRandomAccessStream();
            await source.WriteAsync(encoded.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
            source.Seek(0);

            try
            {
                var decoder = await BitmapDecoder.CreateAsync(source).AsTask(cancellationToken).ConfigureAwait(false);
                if (!FitsPixelBudget(decoder.PixelWidth, decoder.PixelHeight)) return null;
                bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore)
                    .AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }
        else if (filter == "FlateDecode")
        {
            if (info.BitsPerComponent != 8) return null;
            if (!FitsPixelBudget(info.Width, info.Height)) return null;
            int pixelCount = checked(info.Width * info.Height);

            BitmapPixelFormat format;
            if (info.ColorSpace == "DeviceGray")
            {
                if (encoded.Length != pixelCount) return null;
                format = BitmapPixelFormat.Gray8;
            }
            else if (info.ColorSpace == "DeviceRGB")
            {
                if (encoded.Length == checked(pixelCount * 3))
                {
                    byte[] rgba = new byte[checked(pixelCount * 4)];
                    for (int i = 0; i < pixelCount; i++)
                    {
                        if ((i & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
                        rgba[i * 4] = encoded[i * 3];       // R
                        rgba[i * 4 + 1] = encoded[i * 3 + 1]; // G
                        rgba[i * 4 + 2] = encoded[i * 3 + 2]; // B
                        rgba[i * 4 + 3] = 255;              // A
                    }
                    encoded = rgba;
                    format = BitmapPixelFormat.Rgba8;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return null;
            }

            try
            {
                bitmap = SoftwareBitmap.CreateCopyFromBuffer(encoded.AsBuffer(), format, info.Width, info.Height, BitmapAlphaMode.Ignore);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        if (bitmap == null) return null;

        using (bitmap)
        {
            using var destStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, destStream).AsTask(cancellationToken).ConfigureAwait(false);

            var propertySet = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(quality / 100.0f, global::Windows.Foundation.PropertyType.Single) }
            };

            // Re-create the encoder with properties
            destStream.Seek(0);
            destStream.Size = 0;
            encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, destStream, propertySet).AsTask(cancellationToken).ConfigureAwait(false);

            if (bitmap.PixelWidth > maxWidth || bitmap.PixelHeight > maxHeight)
            {
                double scale = Math.Min((double)maxWidth / bitmap.PixelWidth, (double)maxHeight / bitmap.PixelHeight);
                encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Round(bitmap.PixelWidth * scale));
                encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Round(bitmap.PixelHeight * scale));
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            }

            // Convert to Bgra8 if necessary (JPEG encoder often expects Bgra8 or Rgba8, but actually WIC handles conversions. Still, Bgra8 is safest)
            SoftwareBitmap toEncode = bitmap;
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 && bitmap.BitmapPixelFormat != BitmapPixelFormat.Rgba8 && bitmap.BitmapPixelFormat != BitmapPixelFormat.Gray8)
            {
                toEncode = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            }

            encoder.SetSoftwareBitmap(toEncode);

            try
            {
                await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (toEncode != bitmap) toEncode.Dispose();
                return null;
            }

            if (toEncode != bitmap) toEncode.Dispose();

            byte[] outBytes = new byte[destStream.Size];
            destStream.Seek(0);
            await destStream.ReadAsync(outBytes.AsBuffer(), (uint)destStream.Size, InputStreamOptions.None).AsTask(cancellationToken).ConfigureAwait(false);

            return new PdfImageReplacement(
                outBytes,
                (int)(encoder.BitmapTransform.ScaledWidth > 0 ? encoder.BitmapTransform.ScaledWidth : (uint)bitmap.PixelWidth),
                (int)(encoder.BitmapTransform.ScaledHeight > 0 ? encoder.BitmapTransform.ScaledHeight : (uint)bitmap.PixelHeight),
                bitmap.BitmapPixelFormat == BitmapPixelFormat.Gray8 ? 1 : 3);
        }
    }
}
