using System.Runtime.InteropServices.WindowsRuntime;
using ContextBar.Abstractions.Files;
using ContextBar.Pack.Images;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ContextBar.Pack.Images.Windows;

/// <summary>
/// Image engine on the Windows Imaging Component (no third-party code). Decodes the
/// formats Windows has codecs for (HEIC/WebP/AVIF only when the corresponding
/// Microsoft Store extensions are installed) and encodes JPEG, PNG, BMP, TIFF and GIF.
/// Pixels are re-encoded from scratch, so metadata is never carried over.
/// </summary>
public sealed class WindowsImagingEngine : IImageEngine
{
    private static readonly Lazy<HashSet<string>> InstalledDecoderExtensions = new(LoadDecoderExtensions);

    public string Name => "Windows Imaging Component";

    public bool CanPreserveMetadata => false;
    public bool CanRemoveGpsOnly => false;

    public bool CanDecode(string sourceFormat) => sourceFormat switch
    {
        FileFormats.Jpeg or FileFormats.Png or FileFormats.Gif or FileFormats.Bmp or FileFormats.Tiff or FileFormats.Ico => true,
        FileFormats.Heic => InstalledDecoderExtensions.Value.Contains(".heic"),
        FileFormats.WebP => InstalledDecoderExtensions.Value.Contains(".webp"),
        FileFormats.Avif => InstalledDecoderExtensions.Value.Contains(".avif"),
        _ => false
    };

    public bool CanEncode(string targetFormat) => targetFormat is FileFormats.Jpeg or FileFormats.Png or FileFormats.Bmp or FileFormats.Tiff or FileFormats.Gif;

    public async Task TransformAsync(Stream source, Stream destination, ImageTransformRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(request);
        if (!request.StripMetadata || request.RemoveGpsOnly)
            throw new FileProcessingException(ContextBar.Pack.Images.Windows.Resources.Strings.ImagingCannotPreserveMetadata);

        using var input = new InMemoryRandomAccessStream();
        using (var buffer = new MemoryStream())
        {
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            await input.WriteAsync(buffer.ToArray().AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
        }
        input.Seek(0);

        BitmapDecoder decoder;
        SoftwareBitmap bitmap;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken).ConfigureAwait(false);
            bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileProcessingException(ContextBar.Pack.Images.Windows.Resources.Strings.ImageDecodeFailed, ex);
        }

        using (bitmap)
        {
            if (request.TargetFormat is FileFormats.Jpeg or FileFormats.Bmp)
                FlattenOntoWhite(bitmap);

            var (width, height) = TargetSize(bitmap.PixelWidth, bitmap.PixelHeight, request);

            using var output = new InMemoryRandomAccessStream();
            var properties = new BitmapPropertySet();
            if (request.TargetFormat == FileFormats.Jpeg)
            {
                float quality = request.Quality is > 0 and <= 100 ? request.Quality / 100f : 0.9f;
                properties.Add("ImageQuality", new BitmapTypedValue(quality, global::Windows.Foundation.PropertyType.Single));
            }

            try
            {
                var encoder = await BitmapEncoder.CreateAsync(EncoderId(request.TargetFormat), output, properties).AsTask(cancellationToken).ConfigureAwait(false);
                encoder.SetSoftwareBitmap(bitmap);
                if (width != bitmap.PixelWidth || height != bitmap.PixelHeight)
                {
                    encoder.BitmapTransform.ScaledWidth = (uint)width;
                    encoder.BitmapTransform.ScaledHeight = (uint)height;
                    encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                }
                await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new FileProcessingException(ContextBar.Pack.Images.Windows.Resources.Strings.ImageEncodeFailed, ex);
            }

            output.Seek(0);
            using var result = output.AsStreamForRead();
            await result.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static (int Width, int Height) TargetSize(int width, int height, ImageTransformRequest request)
    {
        if (request.MaxWidth is not int maxWidth || request.MaxHeight is not int maxHeight || maxWidth <= 0 || maxHeight <= 0)
            return (width, height);
        double ratio = Math.Min((double)maxWidth / width, (double)maxHeight / height);
        if (ratio >= 1.0 && !request.AllowUpscale)
            return (width, height);
        return (Math.Max(1, (int)Math.Round(width * ratio)), Math.Max(1, (int)Math.Round(height * ratio)));
    }

    private static void FlattenOntoWhite(SoftwareBitmap bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        bool changed = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte alpha = pixels[i + 3];
            if (alpha == 255) continue;
            int add = 255 - alpha; // premultiplied "over white"
            pixels[i] = (byte)Math.Min(255, pixels[i] + add);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + add);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + add);
            pixels[i + 3] = 255;
            changed = true;
        }
        if (changed) bitmap.CopyFromBuffer(pixels.AsBuffer());
    }

    private static Guid EncoderId(string format) => format switch
    {
        FileFormats.Jpeg => BitmapEncoder.JpegEncoderId,
        FileFormats.Png => BitmapEncoder.PngEncoderId,
        FileFormats.Bmp => BitmapEncoder.BmpEncoderId,
        FileFormats.Tiff => BitmapEncoder.TiffEncoderId,
        FileFormats.Gif => BitmapEncoder.GifEncoderId,
        _ => throw new FileProcessingException(ContextBar.Pack.Images.Windows.Resources.Strings.ImageUnsupportedFormat)
    };

    private static HashSet<string> LoadDecoderExtensions()
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var info in BitmapDecoder.GetDecoderInformationEnumerator())
            {
                foreach (var extension in info.FileExtensions) extensions.Add(extension);
            }
        }
        catch (Exception)
        {
            // Codec enumeration failing must not break image actions; built-in codecs remain usable.
        }
        return extensions;
    }
}
