using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ContextBar.Pack.Pdf.Windows;

/// <summary>PDF-owned WIC conversion. No dependency on the optional Images pack or its adapters.</summary>
public sealed class WicImageToJpegConverter : IImageToJpegConverter
{
    private static readonly Lazy<HashSet<string>> Extensions = new(() =>
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var codec in BitmapDecoder.GetDecoderInformationEnumerator()) foreach (string extension in codec.FileExtensions) result.Add(extension); }
        catch { }
        return result;
    });

    public bool CanConvert(string format) => format switch
    {
        FileFormats.Jpeg or FileFormats.Png or FileFormats.Gif or FileFormats.Bmp or FileFormats.Tiff or FileFormats.Ico => true,
        FileFormats.Heic => Extensions.Value.Contains(".heic"),
        FileFormats.WebP => Extensions.Value.Contains(".webp"),
        FileFormats.Avif => Extensions.Value.Contains(".avif"),
        _ => false
    };

    public async Task ConvertAsync(string path, string format, Stream jpeg, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanConvert(format)) throw new InvalidDataException();
            await using var source = File.OpenRead(path);
            using var input = new InMemoryRandomAccessStream();
            using var inputWriter = input.AsStreamForWrite();
            await source.CopyToAsync(inputWriter, cancellationToken).ConfigureAwait(false);
            // The .NET stream adapter buffers small images. Flush before the native
            // decoder reads the underlying random-access stream.
            await inputWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken).ConfigureAwait(false);
            if (decoder.PixelWidth is 0 or > 30000 || decoder.PixelHeight is 0 or > 30000
                || (long)decoder.PixelWidth * decoder.PixelHeight > 120_000_000) throw new InvalidDataException();
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb)
                .AsTask(cancellationToken).ConfigureAwait(false);
            byte[] pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
            bitmap.CopyToBuffer(pixels.AsBuffer());
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if ((i & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
                int alpha = pixels[i + 3];
                for (int channel = 0; channel < 3; channel++)
                    pixels[i + channel] = (byte)((pixels[i + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
                pixels[i + 3] = 255;
            }
            using var output = new InMemoryRandomAccessStream();
            var properties = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(0.92f, global::Windows.Foundation.PropertyType.Single) } };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, properties).AsTask(cancellationToken).ConfigureAwait(false);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
            output.Seek(0);
            await output.AsStreamForRead().CopyToAsync(jpeg, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new FileProcessingException(Resources.Strings.ImageConversionFailed, ex); }
    }
}
