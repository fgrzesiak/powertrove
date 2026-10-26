using FileKit.Imaging;

namespace PowerTrove.Pack.Images;

public enum ImageTarget
{
    /// <summary>Keep the source format when it can be encoded, otherwise JPEG (opaque sources) or PNG.</summary>
    SameAsSource,
    Jpeg,
    Png,
    WebP,
    Avif
}

/// <summary>Declarative description of an image action: which options it exposes and their defaults.</summary>
public sealed record ImageExportProfile(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string> SearchTerms,
    ImageTarget Target,
    string OutputSuffix = "",
    int? QualityDefault = null,
    bool ExposeQuality = false,
    int? MaxWidthDefault = null,
    int? MaxHeightDefault = null,
    bool ExposeSize = false,
    bool ExposeMaxDimension = false,
    bool StripMetadataDefault = true,
    bool ExposeStripMetadata = false);

public sealed record ImageExportOptions(int Quality, int? MaxWidth, int? MaxHeight, bool AllowUpscale, bool StripMetadata);

public sealed record ImageLimits
{
    public long MaxPixels { get; init; } = 120_000_000;
    public int MaxDimension { get; init; } = 30_000;
    public long MaxFileBytes { get; init; } = 300L * 1024 * 1024;

    public static ImageLimits Default { get; } = new();
}

/// <summary>
/// One configurable image action (convert, resize, compress, strip metadata, presets).
/// Validates real dimensions from the header before any decoder runs, so pixel-flood
/// images are rejected without allocating their pixel buffers.
/// </summary>
public sealed class ImageExportAction : PerFileAction<ImageExportOptions>
{
    internal static readonly string[] SupportedInputExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".avif", ".heic", ".heif" };
    internal static readonly string[] InputFormats = { FileFormats.Jpeg, FileFormats.Png, FileFormats.Gif, FileFormats.Bmp, FileFormats.Tiff, FileFormats.WebP, FileFormats.Avif, FileFormats.Heic };

    private readonly ImageExportProfile _profile;
    private readonly CompositeImageEngine _engine;
    private readonly ImageLimits _limits;
    private readonly FileInputFilter _input;

    public ImageExportAction(ImageExportProfile profile, CompositeImageEngine engine, FileActionServices services, ImageLimits? limits = null)
        : base(services)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _limits = limits ?? ImageLimits.Default;
        _input = new FileInputFilter(SR.GetString("Image_Files"), SupportedInputExtensions, InputFormats);

        Descriptor = new ActionDescriptor(
            Id: profile.Id,
            Title: profile.Title,
            Description: profile.Description,
            Accepts: new HashSet<ContentKind> { ContentKind.Files },
            SearchTerms: profile.SearchTerms,
            RequiresPreview: true,
            ChangesFiles: true,
            Parameters: BuildParameters(profile),
            Selection: SelectionRequirements.Any("image"));
    }

    public override ActionDescriptor Descriptor { get; }

    public ImageExportProfile Profile => _profile;

    protected override FileInputFilter Input => _input;

    protected override bool ReportSizeChange => true;

    protected override string ProgressVerb => SR.GetString("Image_Operation_Progress");

    protected override string? UnavailableReason
    {
        get
        {
            string? target = TargetFormatId(_profile.Target);
            if (target is not null && !_engine.CanEncode(target))
                return string.Format(SR.GetString("ImageExportAction_Unavailable"), TargetName(_profile.Target));
            return null;
        }
    }

    protected override ImageExportOptions ReadOptions(ActionParameterValues values)
    {
        int quality = _profile.ExposeQuality ? values.GetInt32("Quality") : _profile.QualityDefault ?? 0;
        int? maxWidth = _profile.MaxWidthDefault, maxHeight = _profile.MaxHeightDefault;
        if (_profile.ExposeSize)
        {
            maxWidth = values.GetInt32("MaxWidth");
            maxHeight = values.GetInt32("MaxHeight");
        }
        else if (_profile.ExposeMaxDimension)
        {
            int max = values.GetInt32("MaxDimension");
            maxWidth = max > 0 ? max : null;
            maxHeight = max > 0 ? max : null;
        }
        bool allowUpscale = _profile.ExposeSize && values.GetBoolean("AllowUpscale");
        bool strip = _profile.ExposeStripMetadata ? values.GetBoolean("StripMetadata") : _profile.StripMetadataDefault;
        return new ImageExportOptions(quality, maxWidth, maxHeight, allowUpscale, strip);
    }

    protected override string DescribeOperation(int inputCount, ImageExportOptions options)
    {
        var parts = new List<string>();
        string verb = _profile.Target == ImageTarget.SameAsSource ? SR.GetString("ImageExportAction_Op_WillProcess") : SR.GetString("ImageExportAction_Op_WillConvert");
        string target = _profile.Target == ImageTarget.SameAsSource ? string.Empty : string.Format(SR.GetString("ImageExportAction_Op_To"), TargetName(_profile.Target));
        parts.Add($"{verb} {Plural(inputCount, SR.GetString("Image_File"), SR.GetString("Image_Files"))}{target}");
        if (options.MaxWidth is int w && options.MaxHeight is int h) parts.Add(string.Format(SR.GetString("ImageExportAction_Op_FitWithin"), w, h) + (options.AllowUpscale ? string.Empty : SR.GetString("ImageExportAction_Op_NoEnlargement")));
        if (options.Quality > 0 && IsLossyTarget()) parts.Add(string.Format(SR.GetString("ImageExportAction_Op_Quality"), options.Quality));
        parts.Add(options.StripMetadata ? SR.GetString("ImageExportAction_Op_RemoveMeta") : SR.GetString("ImageExportAction_Op_KeepMeta"));
        return string.Join(", ", parts) + ".";
    }

    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, ImageExportOptions options)
    {
        if (inputs.Any(f => string.Equals(f.Extension, ".gif", StringComparison.OrdinalIgnoreCase) || string.Equals(f.Extension, ".webp", StringComparison.OrdinalIgnoreCase)))
            yield return SR.GetString("ImageExportAction_Warn_Animation");
        if (_profile.Target == ImageTarget.Jpeg && inputs.Any(f => FormatFromExtension(f.Extension) is FileFormats.Png or FileFormats.Gif or FileFormats.WebP))
            yield return SR.GetString("ImageExportAction_Warn_Transparency");
        if (!options.StripMetadata && !_engine.CanPreserveMetadata)
            yield return SR.GetString("ImageExportAction_Warn_PreserveMetadata");
    }

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, ImageExportOptions options)
    {
        string? sourceFormat = FormatFromExtension(input.Extension);
        yield return new PlannedOutput(
            Path.GetFileNameWithoutExtension(input.Name) + _profile.OutputSuffix,
            ExtensionFor(ResolveTarget(sourceFormat)));
    }

    protected override async Task ProcessAsync(FileWorkItem item, ImageExportOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        if (Services.FileSystem.GetFileLength(item.Path) > _limits.MaxFileBytes)
            throw new FileProcessingException(SR.GetString("ImageExportAction_Err_TooLarge"));

        string sourceFormat = item.DetectedFormat ?? FormatFromExtension(item.File.Extension)
            ?? throw new FileProcessingException(SR.GetString("ImageExportAction_Err_UnknownFormat"));

        await using (var probeStream = Services.FileSystem.OpenRead(item.Path))
        {
            ImageInfo info;
            try
            {
                info = await ImageProbe.ProbeAsync(probeStream, cancellationToken).ConfigureAwait(false);
            }
            catch (ImageProbeException ex)
            {
                throw new FileProcessingException(ex.Message, ex);
            }
            if (info.PixelCount > _limits.MaxPixels || info.Width > _limits.MaxDimension || info.Height > _limits.MaxDimension)
                throw new FileProcessingException(SR.GetString("ImageExportAction_Err_Dimensions"));
        }

        string target = ResolveTarget(sourceFormat);
        var request = new ImageTransformRequest(sourceFormat, target, options.Quality, options.MaxWidth, options.MaxHeight, options.AllowUpscale, options.StripMetadata);
        if (_engine.Select(request) is null)
            throw new FileProcessingException(string.Format(SR.GetString("ImageExportAction_Err_NoCodec"), sourceFormat.ToUpperInvariant(), target.ToUpperInvariant()));

        await output.WriteFileAsync(item.Directory, item.BaseName + _profile.OutputSuffix, ExtensionFor(target), async (destination, ct) =>
        {
            await using var source = Services.FileSystem.OpenRead(item.Path);
            await _engine.TransformAsync(source, destination, request, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private string ResolveTarget(string? sourceFormat)
    {
        if (TargetFormatId(_profile.Target) is string explicitTarget)
            return explicitTarget;
        if (sourceFormat is not null && _engine.CanEncode(sourceFormat) && sourceFormat is not (FileFormats.Heic or FileFormats.Gif))
            return sourceFormat;
        // HEIC/GIF and unencodable sources fall back to widely compatible formats.
        return sourceFormat is FileFormats.Jpeg or FileFormats.Heic or FileFormats.Avif ? FileFormats.Jpeg : FileFormats.Png;
    }

    private bool IsLossyTarget() => _profile.Target is ImageTarget.Jpeg or ImageTarget.WebP or ImageTarget.Avif or ImageTarget.SameAsSource;

    internal static string? TargetFormatId(ImageTarget target) => target switch
    {
        ImageTarget.Jpeg => FileFormats.Jpeg,
        ImageTarget.Png => FileFormats.Png,
        ImageTarget.WebP => FileFormats.WebP,
        ImageTarget.Avif => FileFormats.Avif,
        _ => null
    };

    private static string TargetName(ImageTarget target) => target switch
    {
        ImageTarget.Jpeg => "JPEG",
        ImageTarget.Png => "PNG",
        ImageTarget.WebP => "WebP",
        ImageTarget.Avif => "AVIF",
        _ => SR.GetString("ImageTarget_Original")
    };

    internal static string? FormatFromExtension(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => FileFormats.Jpeg,
        ".png" => FileFormats.Png,
        ".gif" => FileFormats.Gif,
        ".bmp" => FileFormats.Bmp,
        ".tif" or ".tiff" => FileFormats.Tiff,
        ".webp" => FileFormats.WebP,
        ".avif" => FileFormats.Avif,
        ".heic" or ".heif" => FileFormats.Heic,
        _ => null
    };

    internal static string ExtensionFor(string format) => format switch
    {
        FileFormats.Jpeg => ".jpg",
        FileFormats.Png => ".png",
        FileFormats.Gif => ".gif",
        FileFormats.Bmp => ".bmp",
        FileFormats.Tiff => ".tif",
        FileFormats.WebP => ".webp",
        FileFormats.Avif => ".avif",
        FileFormats.Heic => ".heic",
        _ => "." + format
    };

    private static IReadOnlyList<ActionParameterDefinition>? BuildParameters(ImageExportProfile profile)
    {
        var list = new List<ActionParameterDefinition>();
        if (profile.ExposeQuality)
            list.Add(ActionParameterDefinition.Integer("Quality", SR.GetString("ImageExportAction_Param_Quality_Title"), profile.QualityDefault ?? 80, 1, 100, SR.GetString("ImageExportAction_Param_Quality_Desc")));
        if (profile.ExposeSize)
        {
            list.Add(ActionParameterDefinition.Integer("MaxWidth", SR.GetString("ImageExportAction_Param_MaxWidth_Title"), profile.MaxWidthDefault ?? 1920, 1, 30000));
            list.Add(ActionParameterDefinition.Integer("MaxHeight", SR.GetString("ImageExportAction_Param_MaxHeight_Title"), profile.MaxHeightDefault ?? 1920, 1, 30000));
            list.Add(ActionParameterDefinition.Boolean("AllowUpscale", SR.GetString("ImageExportAction_Param_AllowUpscale_Title"), false));
        }
        if (profile.ExposeMaxDimension)
            list.Add(ActionParameterDefinition.Integer("MaxDimension", SR.GetString("ImageExportAction_Param_MaxDimension_Title"), profile.MaxWidthDefault ?? 2560, 0, 30000));
        if (profile.ExposeStripMetadata)
            list.Add(ActionParameterDefinition.Boolean("StripMetadata", SR.GetString("ImageExportAction_Param_StripMetadata_Title"), profile.StripMetadataDefault));
        return list.Count == 0 ? null : list;
    }
}
