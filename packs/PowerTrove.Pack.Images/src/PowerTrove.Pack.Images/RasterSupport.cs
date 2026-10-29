using System.Globalization;
using FileKit.Imaging;
using FileKit.Imaging.Raster;

namespace PowerTrove.Pack.Images;

/// <summary>Localized messages shared with codec adapters, without exposing resource implementation details.</summary>
public static class RasterMessages
{
    public static string DecodeFailed => SR.GetString("Raster_DecodeFailed");
    public static string EncodeFailed => SR.GetString("Raster_EncodeFailed");
}

internal static class RasterSupport
{
    public static FileInputFilter Filter => new(SR.GetString("Image_Files"), ImageExportAction.SupportedInputExtensions, ImageExportAction.InputFormats);
    public static string Format(SelectedFile file) => ImageExportAction.FormatFromExtension(file.Extension) ?? throw new FileProcessingException(SR.GetString("ImageExportAction_Err_UnknownFormat"));

    /// <summary>
    /// Output format for an action that keeps the source format: the source when it can be written here, otherwise
    /// JPEG for photo formats (HEIC, AVIF) and PNG for the rest or when transparency must survive. HEIC, for
    /// example, can be read everywhere but written only with Windows' optional HEVC extension.
    /// </summary>
    public static string KeepFormat(string source, Func<string, bool> canEncode, bool transparency = false) =>
        canEncode(source) ? source
        : !transparency && source is FileFormats.Heic or FileFormats.Avif or FileFormats.Jpeg ? FileFormats.Jpeg : FileFormats.Png;

    /// <summary>A preview note when a source format cannot be written and results use another format.</summary>
    public static IEnumerable<string> KeepFormatWarnings(IEnumerable<SelectedFile> files, Func<string, bool> canEncode, bool transparency = false) =>
        OutputFormatWarnings(files, SameFormat, canEncode, transparency);

    /// <summary>The choice value that keeps each file's own format.</summary>
    public const string SameFormat = "same";

    /// <summary>"Output format" option: the source format by default, or one of the formats PowerTrove can write.</summary>
    public static ActionParameterDefinition OutputFormatParameter() =>
        Choice("OutputFormat", SameFormat, SameFormat, FileFormats.Png, FileFormats.Jpeg, FileFormats.WebP, FileFormats.Avif, FileFormats.Heic);

    /// <summary>The format actually written for <paramref name="source"/> when <paramref name="chosen"/> was selected.</summary>
    public static string OutputFormat(string source, string? chosen, Func<string, bool> canEncode, bool transparency = false) =>
        string.IsNullOrEmpty(chosen) || chosen == SameFormat ? KeepFormat(source, canEncode, transparency) : KeepFormat(chosen, canEncode, transparency);

    /// <summary>
    /// Preview notes for requested formats that cannot be written here. For HEIC they link to the HEVC extension
    /// that lets Windows write it, and point to the "Output format" option.
    /// </summary>
    public static IEnumerable<string> OutputFormatWarnings(IEnumerable<SelectedFile> files, string? chosen, Func<string, bool> canEncode, bool transparency = false)
    {
        var requested = string.IsNullOrEmpty(chosen) || chosen == SameFormat
            ? files.Select(f => ImageExportAction.FormatFromExtension(f.Extension)).OfType<string>().Distinct()
            : new[] { chosen };
        foreach (var format in requested)
        {
            string target = KeepFormat(format, canEncode, transparency);
            if (target == format) continue;
            string text = string.Format(System.Globalization.CultureInfo.CurrentCulture, SR.GetString("Raster_SavedAsOtherFormat"),
                format.ToUpperInvariant(), target.ToUpperInvariant());
            if (format == FileFormats.Heic) text += " " + SR.GetString("Raster_HeicNeedsExtension");
            yield return text;
        }
    }
    public static ActionParameterDefinition Choice(string key, string defaultValue, params string[] choices) =>
        ActionParameterDefinition.Choice(key, SR.GetString("Raster_" + key), defaultValue,
            choices.Select(value => new ActionParameterChoice(value, SR.GetString("Raster_Choice_" + value.Replace(':', '_')))).ToArray());
    public static ActionDescriptor Descriptor(string id, string key, IReadOnlyList<ActionParameterDefinition>? parameters = null, int min = 1, int max = 1000, int weight = 0) => new(
        "powertrove.image." + id, SR.GetString("Raster_Title_" + key), SR.GetString("Raster_Description_" + key),
        new HashSet<ContentKind> { ContentKind.Files }, new[] { "image", id, "picture", "photo" },
        RequiresPreview: true, ChangesFiles: true, ExplicitWeight: weight, Parameters: parameters,
        Selection: new SelectionRequirements(new HashSet<string> { "image" }, MinItems: min, MaxItems: max, IdealMinItems: min));
    public static async Task<ImageInfo> ProbeAsync(FileActionServices services, SelectedFile file, CancellationToken ct, long maxPixels = RasterImage.MaxPixels)
    {
        if (services.FileSystem.GetFileLength(file.Path!) > ImageLimits.Default.MaxFileBytes) throw new FileProcessingException(SR.GetString("ImageExportAction_Err_TooLarge"));
        try
        {
            await using var stream = services.FileSystem.OpenRead(file.Path!);
            var info = await ImageProbe.ProbeAsync(stream, ct).ConfigureAwait(false);
            if (info.PixelCount > maxPixels || info.Width > ImageLimits.Default.MaxDimension || info.Height > ImageLimits.Default.MaxDimension)
                throw new FileProcessingException(SR.GetString("Raster_TooManyPixels"));
            return info;
        }
        catch (ImageProbeException ex) { throw new FileProcessingException(RasterMessages.DecodeFailed, ex); }
    }
    public static async Task<ImageRaster> DecodeAsync(FileActionServices services, IImageRasterCodec codec, FileWorkItem item, CancellationToken ct)
    {
        await ProbeAsync(services, item.File, ct).ConfigureAwait(false);
        string format = item.DetectedFormat ?? Format(item.File);
        if (!codec.CanDecode(format)) throw new FileProcessingException(SR.GetString("Raster_NoCodec"));
        await using var source = services.FileSystem.OpenRead(item.Path);
        var raster = await codec.DecodeAsync(source, format, RasterImage.MaxPixels, ct).ConfigureAwait(false);
        _ = Pixels(raster); // Defend the port boundary before doing any pixel arithmetic.
        return raster;
    }
    public static RasterImage Pixels(ImageRaster raster) => new(raster.Width, raster.Height, raster.Bgra32);
    public static ImageRaster Map(RasterImage raster, ImageRaster? source = null) => new(raster.Width, raster.Height, raster.Bgra, source?.DpiX ?? 96, source?.DpiY ?? 96);
    public static BgraColor Color((byte R, byte G, byte B) color, byte alpha = 255) => new(color.B, color.G, color.R, alpha);
    public static double? Ratio(string ratio)
    {
        string[] parts = ratio.Split(':');
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double w) &&
            double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double h) &&
            double.IsFinite(w) && double.IsFinite(h) && w > 0 && h > 0 && w <= 1_000_000 && h <= 1_000_000 ? w / h : null;
    }
}

public static class AspectCrop
{
    public static RasterRect Calculate(int width, int height, double ratio, string anchor)
    {
        RasterImage.ByteLength(width, height);
        if (!double.IsFinite(ratio) || ratio <= 0 || anchor is not ("center" or "top" or "bottom" or "left" or "right")) throw new RasterException("Invalid crop options.");
        int w = width, h = height;
        if ((double)width / height > ratio) w = Math.Max(1, (int)Math.Floor(height * ratio));
        else h = Math.Max(1, (int)Math.Floor(width / ratio));
        int x = (width - w) / 2, y = (height - h) / 2;
        if (anchor == "left") x = 0; if (anchor == "right") x = width - w;
        if (anchor == "top") y = 0; if (anchor == "bottom") y = height - h;
        return new RasterRect(x, y, w, h);
    }
}
