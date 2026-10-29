using System.Globalization;
using PowerTrove.Abstractions.Files;
using PowerTrove.Pack.Images;
using FileKit.Imaging;
using ImageMagick;

namespace PowerTrove.Pack.Images.Magick;

public sealed class MagickImageWatermarker(string preferredFont = "Segoe UI") : IImageWatermarker
{
    public Task ValidateAsync(Stream watermark, string format, CancellationToken ct) => Task.Run(async () =>
    {
        using var image = await ReadAsync(watermark, format, ct).ConfigureAwait(false);
    }, ct);

    public Task ApplyAsync(Stream source, Stream? watermark, Stream destination, ImageWatermarkRequest request, CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            if (request.Opacity is < 1 or > 100 || request.FontSizePercent is < 2 or > 30 || request.WidthPercent is < 5 or > 80) throw new InvalidDataException();
            using var image = await ReadAsync(source, request.SourceFormat, ct).ConfigureAwait(false);
            uint quality = image.Quality; image.AutoOrient(); image.Strip();
            using var mark = watermark is null ? TextMark(image, request) : await ReadAsync(watermark, request.WatermarkFormat!, ct).ConfigureAwait(false);
            if (watermark is not null)
            {
                mark.AutoOrient(); mark.Strip();
                mark.Resize((uint)Math.Max(1, image.Width * request.WidthPercent / 100), (uint)Math.Max(1, image.Height * 0.8));
            }
            int margin = Math.Max(1, (int)Math.Min(image.Width, image.Height) / 50);
            void Composite(int x, int y)
            {
                ct.ThrowIfCancellationRequested();
                if (watermark is null) image.Composite(mark, x, y, CompositeOperator.Over);
                else image.Composite(mark, x, y, CompositeOperator.Dissolve, request.Opacity.ToString(CultureInfo.InvariantCulture));
            }
            if (request.Position == "tiled")
            {
                int stepX = Math.Max((int)mark.Width + margin * 2, (int)Math.Ceiling(image.Width / 32.0));
                int stepY = Math.Max((int)mark.Height + margin * 2, (int)Math.Ceiling(image.Height / 32.0));
                for (int y = 0; y < image.Height; y += stepY) for (int x = 0; x < image.Width; x += stepX) Composite(x, y);
            }
            else
            {
                var (x, y) = Placement((int)image.Width, (int)image.Height, (int)mark.Width, (int)mark.Height, margin, request.Position);
                Composite(x, y);
            }
            string target = request.TargetFormat ?? request.SourceFormat;
            image.Format = Format(target);
            if (target == FileFormats.Jpeg)
            {
                image.BackgroundColor = MagickColors.White; image.Alpha(AlphaOption.Remove); image.Quality = quality > 0 ? quality : 90;
            }
            ct.ThrowIfCancellationRequested(); await image.WriteAsync(destination, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not FileProcessingException)
        { throw new FileProcessingException(ExtendedImageMessages.Get("WatermarkFailed"), ex); }
    }, ct);

    private MagickImage TextMark(MagickImage image, ImageWatermarkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 500 || request.Text.Any(char.IsControl)) throw new FileProcessingException(ExtendedImageMessages.Get("WatermarkTextInvalid"));
        string? font = new[] { preferredFont, "Arial", "Liberation Sans", "DejaVu Sans" }.FirstOrDefault(name => MagickNET.FontFamilies.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (font is null) throw new FileProcessingException(ExtendedImageMessages.Get("FontUnavailable"));
        using var measure = new MagickImage(MagickColors.Transparent, 1, 1);
        measure.Settings.FontFamily = font; measure.Settings.FontPointsize = Math.Max(1, image.Width * request.FontSizePercent / 100.0);
        string literal = request.Text.Replace("\\", "\\\\").Replace("%", "%%");
        var metrics = measure.FontTypeMetrics(literal) ?? throw new FileProcessingException(ExtendedImageMessages.Get("FontUnavailable"));
        double fit = Math.Min(1, Math.Min(image.Width * 0.9 / Math.Max(1, metrics.TextWidth), image.Height * 0.9 / Math.Max(1, metrics.TextHeight)));
        measure.Settings.FontPointsize = Math.Max(1, measure.Settings.FontPointsize * fit);
        metrics = measure.FontTypeMetrics(literal) ?? throw new FileProcessingException(ExtendedImageMessages.Get("FontUnavailable"));
        uint width = (uint)Math.Clamp((int)Math.Ceiling(metrics.TextWidth + 4), 1, (int)image.Width), height = (uint)Math.Clamp((int)Math.Ceiling(metrics.TextHeight + 4), 1, (int)image.Height);
        var mark = new MagickImage(MagickColors.Transparent, width, height);
        try
        {
            mark.Settings.FontFamily = font; mark.Settings.FontPointsize = measure.Settings.FontPointsize;
            if (request.Color.Length != 7 || request.Color[0] != '#' || request.Color[1..].Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException();
            mark.Settings.FillColor = new MagickColor(request.Color + ((int)Math.Round(255 * request.Opacity / 100.0)).ToString("X2", CultureInfo.InvariantCulture));
            mark.Annotate(literal, Gravity.Center); return mark;
        }
        catch { mark.Dispose(); throw; }
    }
    public static (int X, int Y) Placement(int width, int height, int markWidth, int markHeight, int margin, string position)
    {
        int left = Math.Min(margin, Math.Max(0, width - markWidth)), top = Math.Min(margin, Math.Max(0, height - markHeight));
        int right = Math.Max(0, width - markWidth - margin), bottom = Math.Max(0, height - markHeight - margin);
        int centerX = Math.Max(0, (width - markWidth) / 2), centerY = Math.Max(0, (height - markHeight) / 2);
        return position switch {
            "top-left" => (left, top), "top" => (centerX, top), "top-right" => (right, top),
            "left" => (left, centerY), "center" => (centerX, centerY), "right" => (right, centerY),
            "bottom-left" => (left, bottom), "bottom" => (centerX, bottom), "bottom-right" => (right, bottom),
            _ => throw new InvalidDataException() };
    }
    private static async Task<MagickImage> ReadAsync(Stream source, string format, CancellationToken ct)
    {
        if (!source.CanSeek || source.Length - source.Position > ImageLimits.Default.MaxFileBytes) throw new FileProcessingException(ExtendedImageMessages.Get("WatermarkImageInvalid"));
        long start = source.Position;
        try
        {
            var info = await ImageProbe.ProbeAsync(source, ct).ConfigureAwait(false); source.Position = start;
            if (info.PixelCount > 60_000_000 || info.Width > 30000 || info.Height > 30000) throw new ImageProbeException("Limit");
            var image = new MagickImage(source, new MagickReadSettings { Format = Format(format), FrameIndex = 0, FrameCount = 1 });
            try { ct.ThrowIfCancellationRequested(); return image; }
            catch { image.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not FileProcessingException)
        { throw new FileProcessingException(ExtendedImageMessages.Get("WatermarkImageInvalid"), ex); }
    }
    private static MagickFormat Format(string format) => format switch {
        FileFormats.Jpeg => MagickFormat.Jpeg, FileFormats.Png => MagickFormat.Png, FileFormats.WebP => MagickFormat.WebP, FileFormats.Avif => MagickFormat.Avif,
        FileFormats.Bmp => MagickFormat.Bmp, FileFormats.Tiff => MagickFormat.Tiff, FileFormats.Gif => MagickFormat.Gif, FileFormats.Heic => MagickFormat.Heic,
        _ => throw new FileProcessingException(ExtendedImageMessages.Get("WatermarkImageInvalid")) };
}
