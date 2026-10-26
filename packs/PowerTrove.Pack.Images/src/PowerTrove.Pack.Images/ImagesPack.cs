using PowerTrove.Abstractions.Ocr;

namespace PowerTrove.Pack.Images;

/// <summary>Image tools. Action ids are stable because preferences, aliases and history refer to them.</summary>
public sealed class ImagesPack : IActionPack
{
    public ImagesPack(CompositeImageEngine engine, FileActionServices services, IEnumerable<ImageExportProfile>? presets = null, ImageLimits? limits = null, IImageRasterCodec? rasterCodec = null,
        IImageLosslessTransformer? losslessTransformer = null, IImageWatermarker? watermarker = null, IAnimatedGifEncoder? gifEncoder = null,
        ITextRecognizer? textRecognizer = null, IFaceDetector? faceDetector = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(services);
        var actions = BuiltInProfiles
            .Select(profile => (IAction)new ImageExportAction(profile, engine, services, limits))
            .ToList();
        actions.AddRange(Enum.GetValues<RasterOperation>().Select(operation => (IAction)new RasterFileAction(operation, services, rasterCodec, engine)));
        actions.Add(new TargetSizeAction(engine, services));
        actions.Add(new CombineImagesAction(false, services, rasterCodec));
        actions.Add(new CombineImagesAction(true, services, rasterCodec));
        actions.Add(new ImageInfoAction(services));
        actions.AddRange(new (string Id, OrthogonalTransform Transform, bool Auto)[] {
            ("rotate-right", OrthogonalTransform.Rotate90, false), ("rotate-left", OrthogonalTransform.Rotate270, false),
            ("rotate-180", OrthogonalTransform.Rotate180, false), ("flip-horizontal", OrthogonalTransform.FlipHorizontal, false),
            ("flip-vertical", OrthogonalTransform.FlipVertical, false), ("auto-rotate", OrthogonalTransform.Identity, true)
        }.Select(x => (IAction)new RotationAction(x.Id, x.Transform, x.Auto, services, rasterCodec, losslessTransformer)));
        actions.Add(new WatermarkAction(false, services, watermarker));
        actions.Add(new WatermarkAction(true, services, watermarker));
        actions.Add(new AnimatedGifAction(services, rasterCodec, gifEncoder));
        actions.Add(new ImageOcrAction(services, rasterCodec, textRecognizer));
        actions.Add(new BlurFacesAction(services, rasterCodec, faceDetector));
        actions.Add(new CompareImagesAction(services, rasterCodec!));
        actions.Add(new RemoveLocationAction(engine, services));
        // Keep saved presets last for hosts which enumerate or display them in registration order.
        actions.AddRange((presets ?? Array.Empty<ImageExportProfile>()).Select(profile => (IAction)new ImageExportAction(profile, engine, services, limits)));
        Actions = actions;
    }

    public ActionPackInfo Info { get; } = Describe();

    internal static ActionPackInfo Describe() => new("contextbar.images", SR.GetString("ImagesPack_Title"), "0.1.0", "PowerTrove");

    public IReadOnlyList<IAction> Actions { get; }

    public static IReadOnlyList<ImageExportProfile> BuiltInProfiles => new[]
    {
        new ImageExportProfile(
            "contextbar.image.convert-webp", SR.GetString("ImagesPack_Title_ConvertWebP"),
            SR.GetString("ImagesPack_Desc_ConvertWebP"),
            new[] { "image", "convert", "webp" },
            ImageTarget.WebP, QualityDefault: 80, ExposeQuality: true, ExposeStripMetadata: true),
        new ImageExportProfile(
            "contextbar.image.convert-avif", SR.GetString("ImagesPack_Title_ConvertAvif"),
            SR.GetString("ImagesPack_Desc_ConvertAvif"),
            new[] { "image", "convert", "avif" },
            ImageTarget.Avif, QualityDefault: 60, ExposeQuality: true, ExposeStripMetadata: true),
        new ImageExportProfile(
            "contextbar.image.convert-jpeg", SR.GetString("ImagesPack_Title_ConvertJpeg"),
            SR.GetString("ImagesPack_Desc_ConvertJpeg"),
            new[] { "image", "convert", "jpeg", "jpg", "heic", "iphone" },
            ImageTarget.Jpeg, QualityDefault: 88, ExposeQuality: true, ExposeStripMetadata: true),
        new ImageExportProfile(
            "contextbar.image.convert-png", SR.GetString("ImagesPack_Title_ConvertPng"),
            SR.GetString("ImagesPack_Desc_ConvertPng"),
            new[] { "image", "convert", "png", "lossless" },
            ImageTarget.Png, ExposeStripMetadata: true),
        new ImageExportProfile(
            "contextbar.image.resize", SR.GetString("ImagesPack_Title_Resize"),
            SR.GetString("ImagesPack_Desc_Resize"),
            new[] { "image", "resize", "scale", "shrink", "dimensions" },
            ImageTarget.SameAsSource, OutputSuffix: ".resized",
            MaxWidthDefault: 1920, MaxHeightDefault: 1920, ExposeSize: true,
            StripMetadataDefault: false, ExposeStripMetadata: true),
        new ImageExportProfile(
            "contextbar.image.compress", SR.GetString("ImagesPack_Title_Compress"),
            SR.GetString("ImagesPack_Desc_Compress"),
            new[] { "image", "compress", "optimize", "smaller", "reduce", "size" },
            ImageTarget.SameAsSource, OutputSuffix: " (compressed)",
            QualityDefault: 75, ExposeQuality: true, MaxWidthDefault: 2560, ExposeMaxDimension: true),
        new ImageExportProfile(
            "contextbar.image.remove-metadata", SR.GetString("ImagesPack_Title_RemoveMetadata"),
            SR.GetString("ImagesPack_Desc_RemoveMetadata"),
            new[] { "image", "metadata", "exif", "strip", "privacy", "gps", "location" },
            ImageTarget.SameAsSource, OutputSuffix: ".stripped"),
    };

    /// <summary>Builds a fixed-option profile from a saved preset (no options dialog).</summary>
    public static ImageExportProfile FromPreset(string id, string title, string description, string format, int quality, int? maxWidth, int? maxHeight, bool stripMetadata)
    {
        var target = format?.Trim().ToLowerInvariant() switch
        {
            "avif" => ImageTarget.Avif,
            "jpeg" or "jpg" => ImageTarget.Jpeg,
            "png" => ImageTarget.Png,
            _ => ImageTarget.WebP
        };
        return new ImageExportProfile(id, title, description, new[] { "preset", "image", "export", format ?? "webp" }, target,
            QualityDefault: Math.Clamp(quality, 1, 100),
            MaxWidthDefault: maxWidth is > 0 && maxHeight is > 0 ? maxWidth : null,
            MaxHeightDefault: maxWidth is > 0 && maxHeight is > 0 ? maxHeight : null,
            StripMetadataDefault: stripMetadata);
    }
}
