using FileKit.Imaging;
using FileKit.Imaging.Jpeg;
using PowerTrove.Abstractions.Files;
using PowerTrove.Pack.Images;

namespace PowerTrove.Pack.Images.Magick;

/// <summary>
/// JPEG coefficient-domain geometric transforms using FileKit.Imaging.
/// No pixel decode, RGB recompression, native dependency or silent edge cropping.
/// </summary>
public sealed class CoefficientJpegTransformer : IImageLosslessTransformer
{
    public Task TransformAsync(Stream source, Stream destination, LosslessImageRequest request,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!source.CanSeek || source.Length - source.Position > ImageLimits.Default.MaxFileBytes)
                throw Failure("JpegUnsupported");

            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = source.Read(chunk, 0, chunk.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffer.Length + n > ImageLimits.Default.MaxFileBytes)
                    throw Failure("JpegUnsupported");
                buffer.Write(chunk, 0, n);
            }

            byte[] input = buffer.ToArray();
            int probeLength = Math.Min(input.Length, ImageProbe.MaxHeaderBytes);
            var info = ImageProbe.Probe(input.AsSpan(0, probeLength));
            if (info.Format != ImageFormat.Jpeg || info.PixelCount > 60_000_000 ||
                info.Width > 30000 || info.Height > 30000 ||
                info.Orientation != request.ExpectedOrientation)
                throw Failure("JpegUnsupported");

            var operation = request.Transform switch
            {
                OrthogonalTransform.Identity => JpegCoefficientTransform.Identity,
                OrthogonalTransform.Rotate90 => JpegCoefficientTransform.Rotate90,
                OrthogonalTransform.Rotate180 => JpegCoefficientTransform.Rotate180,
                OrthogonalTransform.Rotate270 => JpegCoefficientTransform.Rotate270,
                OrthogonalTransform.FlipHorizontal => JpegCoefficientTransform.FlipHorizontal,
                OrthogonalTransform.FlipVertical => JpegCoefficientTransform.FlipVertical,
                OrthogonalTransform.Transpose => JpegCoefficientTransform.Transpose,
                OrthogonalTransform.Transverse => JpegCoefficientTransform.Transverse,
                _ => throw Failure("JpegUnsupported")
            };

            // Generate complete output before writing anything. A rejected partial
            // MCU, invalid metadata or damaged scan never leaks a partial JPEG.
            byte[] output = JpegCoefficientCodec.Transform(input, operation,
                JpegMetadata.Normalize, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (output.LongLength > ImageLimits.Default.MaxFileBytes)
                throw Failure("JpegUnsupported");
            for (int offset = 0; offset < output.Length; offset += 81920)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(output, offset, Math.Min(81920, output.Length - offset));
            }
        }
        catch (JpegCoefficientException ex) when (ex.Message.Contains(
            "complete MCUs", StringComparison.Ordinal))
        {
            throw new FileProcessingException(ExtendedImageMessages.Get("JpegPartialMcu"), ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or FileProcessingException))
        {
            throw new FileProcessingException(ExtendedImageMessages.Get("JpegFailed"), ex);
        }
    }, cancellationToken);

    private static FileProcessingException Failure(string key) =>
        new(ExtendedImageMessages.Get(key));
}
