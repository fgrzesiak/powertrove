using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions.Files;
using PowerTrove.Pack.Images;
using ImageMagick;

namespace PowerTrove.Pack.Images.Magick;

/// <summary>
/// Magick.NET adapter (prototype dependency, see ADR 0005/0007). Used for formats the
/// operating system cannot encode (WebP, AVIF) and when metadata must be preserved.
/// Reads only the first frame, enforces resource limits and never trusts the file
/// extension: the decoder is pinned to the verified source format.
/// </summary>
public sealed class MagickImageEngine : IImageEngine
{
    private static readonly object LimitsGate = new();
    private static bool _limitsApplied;

    private static readonly string[] Decodable =
    {
        FileFormats.Jpeg, FileFormats.Png, FileFormats.Gif, FileFormats.Bmp,
        FileFormats.Tiff, FileFormats.WebP, FileFormats.Avif, FileFormats.Heic
    };

    public MagickImageEngine()
    {
        ApplyResourceLimits();
    }

    public string Name => "Magick.NET";

    public bool CanPreserveMetadata => true;
    public bool CanRemoveGpsOnly => true;
    public bool SupportsHighQualityResize => true;

    public bool CanDecode(string sourceFormat) => Decodable.Contains(sourceFormat) && IsSupported(sourceFormat, write: false);

    public bool CanEncode(string targetFormat) => targetFormat is FileFormats.Jpeg or FileFormats.Png or FileFormats.WebP or FileFormats.Avif or FileFormats.Tiff or FileFormats.Bmp or FileFormats.Gif
        && IsSupported(targetFormat, write: true);

    public Task TransformAsync(Stream source, Stream destination, ImageTransformRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(request);

        // CPU-bound native work runs off the caller's thread.
        return Task.Run(async () =>
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            cancellationToken.ThrowIfCancellationRequested();

            var settings = new MagickReadSettings
            {
                Format = ToMagickFormat(request.SourceFormat),
                FrameIndex = 0,
                FrameCount = 1
            };

            MagickImage image;
            try
            {
                image = new MagickImage(buffer, settings);
            }
            catch (MagickException ex)
            {
                throw new FileProcessingException(PowerTrove.Pack.Images.Magick.Resources.Strings.ImageDecodeFailed, ex);
            }

            using (image)
            {
                if (request.RemoveGpsOnly && request.StripMetadata)
                    throw new FileProcessingException(ExtendedImageMessages.Get("GpsFailed"));
                if (request.RemoveGpsOnly && !GpsProfiles.Remove(image))
                    throw new FileSkippedException(ExtendedImageMessages.Get("NoGps"));
                uint sourceQuality = image.Quality;
                image.AutoOrient();

                if (request.MaxWidth is int maxWidth && request.MaxHeight is int maxHeight && maxWidth > 0 && maxHeight > 0)
                {
                    double ratio = Math.Min((double)maxWidth / image.Width, (double)maxHeight / image.Height);
                    if (ratio < 1.0 || (request.AllowUpscale && ratio > 1.0))
                    {
                        if (request.HighQualityResize) image.FilterType = FilterType.Lanczos;
                        image.Resize((uint)Math.Max(1, Math.Round(image.Width * ratio)), (uint)Math.Max(1, Math.Round(image.Height * ratio)));
                        if (request.HighQualityResize && ratio > 1.0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            image.UnsharpMask(0, .5, .5, .02, Channels.RGB);
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (request.StripMetadata)
                    image.Strip();

                image.Format = ToMagickFormat(request.TargetFormat);
                if (request.TargetFormat == FileFormats.Jpeg && image.HasAlpha)
                {
                    image.BackgroundColor = MagickColors.White;
                    image.Alpha(AlphaOption.Remove);
                }

                if (request.Quality is > 0 and <= 100)
                    image.Quality = (uint)request.Quality;
                else if (sourceQuality > 0 && request.SourceFormat == request.TargetFormat)
                    image.Quality = sourceQuality; // keep the original quality when re-encoding the same format

                try
                {
                    await image.WriteAsync(destination, cancellationToken).ConfigureAwait(false);
                }
                catch (MagickException ex)
                {
                    throw new FileProcessingException(PowerTrove.Pack.Images.Magick.Resources.Strings.ImageEncodeFailed, ex);
                }
            }
        }, cancellationToken);
    }

    private static void ApplyResourceLimits()
    {
        lock (LimitsGate)
        {
            if (_limitsApplied) return;
            ResourceLimits.Width = 30_000;
            ResourceLimits.Height = 30_000;
            ResourceLimits.Area = 150_000_000UL * 4;
            ResourceLimits.Memory = 2UL * 1024 * 1024 * 1024;
            _limitsApplied = true;
        }
    }

    private static bool IsSupported(string format, bool write)
    {
        var magickFormat = ToMagickFormat(format);
        return MagickNET.SupportedFormats.Any(f => f.Format == magickFormat && (write ? f.SupportsWriting : f.SupportsReading));
    }

    private static MagickFormat ToMagickFormat(string format) => format switch
    {
        FileFormats.Jpeg => MagickFormat.Jpeg,
        FileFormats.Png => MagickFormat.Png,
        FileFormats.Gif => MagickFormat.Gif,
        FileFormats.Bmp => MagickFormat.Bmp,
        FileFormats.Tiff => MagickFormat.Tiff,
        FileFormats.WebP => MagickFormat.WebP,
        FileFormats.Avif => MagickFormat.Avif,
        FileFormats.Heic => MagickFormat.Heic,
        _ => throw new FileProcessingException(PowerTrove.Pack.Images.Magick.Resources.Strings.ImageUnsupportedFormat)
    };
}
