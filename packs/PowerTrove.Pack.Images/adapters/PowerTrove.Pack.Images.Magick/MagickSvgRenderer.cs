using System.Text;
using FileKit.Imaging;
using ImageMagick;

namespace PowerTrove.Pack.Images.Magick;

/// <summary>Only accepts the validated static SVG subset; never passes raw user XML to a decoder.</summary>
public sealed class MagickSvgRenderer : ISvgRenderer
{
    public Task RenderAsync(SvgRasterSource source, Stream destination, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        // Apply the same native resource limits as other image transformations.
        _ = new MagickImageEngine();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var image = new MagickImage(Encoding.UTF8.GetBytes(source.Xml), new MagickReadSettings
            { Format = MagickFormat.Svg, BackgroundColor = MagickColors.Transparent });
            cancellationToken.ThrowIfCancellationRequested();
            if (image.Width != source.Width || image.Height != source.Height) throw new SvgInputException("Unexpected rendered SVG dimensions.");
            image.Format = MagickFormat.Png; image.Strip();
            await image.WriteAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (MagickException ex) { throw new FileProcessingException(ExtendedImageMessages.Get("SvgRenderFailed"), ex); }
    }, cancellationToken);
}
