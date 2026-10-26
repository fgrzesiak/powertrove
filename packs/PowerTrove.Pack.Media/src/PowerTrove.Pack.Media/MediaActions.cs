using System.Globalization;

namespace PowerTrove.Pack.Media;

public sealed record MediaOptions
{
    public string SecondsPerImage { get; init; } = "3";
    public string Size { get; init; } = "1080p";
    public MediaOptions() { }
    public MediaOptions(string seconds, string size) { SecondsPerImage = seconds; Size = size; }
}

/// <summary>Shared implementation of the media actions; each instance is one operation/target pair.</summary>
public sealed class MediaAction : PerFileAction<MediaOptions>
{
    internal static readonly string[] AudioExtensions = { ".mp3", ".wav", ".aac", ".ogg", ".flac", ".m4a", ".wma" };
    internal static readonly string[] AudioFormats = { FileFormats.Mp3, FileFormats.Wav, FileFormats.Aac, FileFormats.Ogg, FileFormats.Flac, FileFormats.IsoMedia, FileFormats.Asf };
    internal static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm" };
    internal static readonly string[] SubtitleExtensions = { ".srt", ".vtt", ".ass", ".ssa" };
    internal static readonly string[] VideoFormats = { FileFormats.IsoMedia, FileFormats.Matroska, FileFormats.Avi, FileFormats.Asf };

    private static readonly HashSet<string> LossyAudio = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".aac", ".ogg", ".m4a", ".wma" };
    private static readonly HashSet<string> LosslessAudio = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".flac" };

    private readonly CompositeMediaEngine _engine;
    private readonly MediaOperation _operation;
    private readonly string _target;
    private readonly string _suffix;
    private readonly FileInputFilter _input;

    private MediaAction(ActionDescriptor descriptor, MediaOperation operation, string target, string suffix, CompositeMediaEngine engine, FileActionServices services)
        : base(services)
    {
        Descriptor = descriptor;
        _operation = operation;
        _target = target;
        _suffix = suffix;
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _input = operation == MediaOperation.ConvertAudio
            ? new FileInputFilter(SR.GetString("MediaAudioFiles"), AudioExtensions, AudioFormats)
            : new FileInputFilter(SR.GetString("MediaVideoFiles"), VideoExtensions, VideoFormats);
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => _input;
    protected override string ProgressVerb => _operation == MediaOperation.ConvertAudio ? SR.GetString("MediaProgressAudio") : SR.GetString("MediaProgressVideo");

    protected override string? UnavailableReason =>
        _engine.CanProduce(_operation, _target, _input.Extensions!)
            ? null
            : _target == MediaTargets.WebM
                ? SR.GetString("MediaWebMUnavailable")
                : SR.GetString("MediaConversionUnavailable");

    protected override MediaOptions ReadOptions(ActionParameterValues values) => new();

    protected override string DescribeOperation(int inputCount, MediaOptions options) => _operation switch
    {
        MediaOperation.ConvertAudio => SR.Format("MediaConvertAudio" + (inputCount == 1 ? "_One" : "_Other"), inputCount, _target.ToUpperInvariant()),
        MediaOperation.ConvertVideo => SR.Format("MediaConvertVideo" + (inputCount == 1 ? "_One" : "_Other"), inputCount, _target.ToUpperInvariant()),
        MediaOperation.ExtractAudio => SR.Format("MediaExtractAudio" + (inputCount == 1 ? "_One" : "_Other"), inputCount, _target.ToUpperInvariant()),
        _ => SR.Count("MediaRemoveAudio", inputCount)
    };

    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, MediaOptions options)
    {
        if (_operation != MediaOperation.ConvertAudio) yield break;
        string targetExtension = "." + _target;
        if (inputs.Any(f => LosslessAudio.Contains(f.Extension) && LossyAudio.Contains(targetExtension)))
            yield return SR.GetString("MediaLosslessToLossy");
        if (inputs.Any(f => LossyAudio.Contains(f.Extension) && LosslessAudio.Contains(targetExtension)))
            yield return SR.GetString("MediaLossyToLossless");
        if (inputs.Any(f => LossyAudio.Contains(f.Extension) && LossyAudio.Contains(targetExtension)))
            yield return SR.GetString("MediaLossyToLossy");
    }

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, MediaOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + _suffix, OutputExtension(input.Extension));
    }

    protected override async Task ProcessAsync(FileWorkItem item, MediaOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        string sourceExtension = Path.GetExtension(item.Path).ToLowerInvariant();
        var request = new MediaRequest(_operation, _target, sourceExtension);
        var engine = _engine.Select(request) ?? throw MediaMessages.Failure("MediaNoEngine");
        await output.WriteFileFromPathAsync(item.Directory, item.BaseName + _suffix, OutputExtension(sourceExtension),
            (destination, ct) => MediaMessages.RunAsync(() => engine.ProcessAsync(item.Path, destination, request, ct), ct), cancellationToken).ConfigureAwait(false);
    }

    private string OutputExtension(string sourceExtension) =>
        _target == MediaTargets.SameAsSource ? sourceExtension.ToLowerInvariant() : "." + _target;

    public static MediaAction ConvertAudio(string target, CompositeMediaEngine engine, FileActionServices services) => new(
        new ActionDescriptor(
            $"powertrove.audio.convert-{target}", SR.Format("MediaActions_Title_ConvertAudio", target.ToUpperInvariant()),
            SR.Format("MediaActions_Desc_ConvertAudio", target.ToUpperInvariant()),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "audio", "convert", target, "music", "sound" },
            RequiresPreview: true, ChangesFiles: true,
            Selection: SelectionRequirements.Any("audio")),
        MediaOperation.ConvertAudio, target, string.Empty, engine, services);

    public static MediaAction ConvertVideo(string target, CompositeMediaEngine engine, FileActionServices services) => new(
        new ActionDescriptor(
            $"powertrove.video.convert-{target}", SR.Format("MediaActions_Title_ConvertAudio", target.ToUpperInvariant()),
            SR.Format("MediaActions_Desc_ConvertVideo", target.ToUpperInvariant()),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "video", "convert", target },
            RequiresPreview: true, ChangesFiles: true,
            Selection: SelectionRequirements.Any("video")),
        MediaOperation.ConvertVideo, target, string.Empty, engine, services);

    public static MediaAction ExtractAudio(CompositeMediaEngine engine, FileActionServices services) => new(
        new ActionDescriptor(
            "powertrove.video.extract-audio", SR.GetString("MediaActions_Title_ExtractAudio"),
            SR.GetString("MediaActions_Desc_ExtractAudio"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "extract", "audio", "mp3", "video", "sound" },
            RequiresPreview: true, ChangesFiles: true,
            Selection: SelectionRequirements.Any("video")),
        MediaOperation.ExtractAudio, MediaTargets.Mp3, string.Empty, engine, services);

    public static MediaAction RemoveAudio(CompositeMediaEngine engine, FileActionServices services) => new(
        new ActionDescriptor(
            "powertrove.video.remove-audio", SR.GetString("MediaActions_Title_RemoveAudio"),
            SR.GetString("MediaActions_Desc_RemoveAudio"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "video", "mute", "remove", "audio", "silent" },
            RequiresPreview: true, ChangesFiles: true,
            Selection: SelectionRequirements.Any("video")),
        MediaOperation.RemoveAudio, MediaTargets.SameAsSource, ".muted", engine, services);
}

/// <summary>Audio and video tools. FFmpeg is optional; Windows Media Foundation covers the common formats.</summary>
public sealed class MediaPack : IActionPack
{
    public MediaPack(CompositeMediaEngine engine, FileActionServices services, IMediaInspector? inspector = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(services);
        inspector ??= new CompositeMediaInspector(Array.Empty<IMediaInspector>());
        Actions = new IAction[]
        {
            MediaAction.ConvertAudio(MediaTargets.Mp3, engine, services),
            MediaAction.ConvertAudio(MediaTargets.M4a, engine, services),
            MediaAction.ConvertAudio(MediaTargets.Flac, engine, services),
            MediaAction.ConvertAudio(MediaTargets.Wav, engine, services),
            MediaAction.ConvertVideo(MediaTargets.Mp4, engine, services),
            MediaAction.ConvertVideo(MediaTargets.WebM, engine, services),
            MediaAction.ExtractAudio(engine, services),
            MediaAction.RemoveAudio(engine, services),
            new MediaInfoAction(inspector, services),
            new JoinMediaAction(engine, inspector, services),
            new MediaToolAction("trim", engine, inspector, services), new MediaToolAction("compress", engine, inspector, services),
            new MediaToolAction("to-gif", engine, inspector, services), new MediaToolAction("extract-frames", engine, inspector, services),
            new MediaToolAction("normalize", engine, inspector, services), new MediaToolAction("rotate", engine, inspector, services),
            new ReplaceAudioAction(engine, services),
            new EmbedSubtitlesAction(engine, services),
            new SlideshowVideoAction(engine, services),
            new StillImageVideoAction(engine, services)
        };
    }

    public ActionPackInfo Info { get; } = Describe();

    internal static ActionPackInfo Describe() => new("powertrove.media", SR.GetString("MediaPack_Title"), "0.1.0", "PowerTrove");

    public IReadOnlyList<IAction> Actions { get; }
}
public sealed class ReplaceAudioAction : CombineFilesAction<MediaOptions>
{
    private readonly CompositeMediaEngine _engine;
    private readonly FileInputFilter _input;

    public ReplaceAudioAction(CompositeMediaEngine engine, FileActionServices services) : base(services)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _input = new FileInputFilter(SR.GetString("MediaFiles"), MediaAction.VideoExtensions.Concat(MediaAction.AudioExtensions).ToArray(), MediaAction.VideoFormats.Concat(MediaAction.AudioFormats).ToArray());

        Descriptor = new ActionDescriptor(
            "powertrove.video.replace-audio",
            SR.GetString("MediaActions_Title_ReplaceAudio"),
            SR.GetString("MediaActions_Desc_ReplaceAudio"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "video", "audio", "replace", "track" },
            RequiresPreview: true, ChangesFiles: true,
            Selection: SelectionRequirements.Any("video", "audio") with
            {
                MinItems = 2,
                AllowOtherItems = false,
                FamilyLimits = new Dictionary<string, FamilyLimit>
                {
                    { "video", new FamilyLimit(1, 1) },
                    { "audio", new FamilyLimit(1, 1) }
                }
            });
    }

    public override ActionDescriptor Descriptor { get; }

    protected override FileInputFilter Input => _input;

    protected override string? UnavailableReason => _engine.CanProduce(MediaOperation.ReplaceAudio, MediaTargets.SameAsSource, MediaAction.VideoExtensions)
        ? null : SR.GetString("Media_RequiresFFmpeg");

    protected override MediaOptions ReadOptions(ActionParameterValues values) => new();

    protected override string DescribeOperation(int inputCount, MediaOptions options) => SR.GetString("MediaActions_Operation_ReplaceAudio");

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, MediaOptions options)
    {
        var video = inputs.First(i => SelectionFamilies.Of(i).Equals("video", StringComparison.OrdinalIgnoreCase));
        string baseName = Path.GetFileNameWithoutExtension(video.Name);
        return new PlannedOutput(baseName + " (new audio)", Path.GetExtension(video.Name).ToLowerInvariant());
    }

    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, MediaOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var byFamily = ByFamily(items);
        var videoItem = byFamily["video"].First();
        var audioItem = byFamily["audio"].First();

        string sourceExtension = Path.GetExtension(videoItem.Path).ToLowerInvariant();
        var request = new MediaRequest(MediaOperation.ReplaceAudio, MediaTargets.SameAsSource, sourceExtension, new[] { audioItem.Path });

        var engine = _engine.Select(request) ?? throw MediaMessages.Failure("MediaNoEngine");

        await output.WriteFileFromPathAsync(videoItem.Directory, videoItem.BaseName + " (new audio)", sourceExtension,
            (destination, ct) => MediaMessages.RunAsync(() => engine.ProcessAsync(videoItem.Path, destination, request, ct), ct), cancellationToken).ConfigureAwait(false);
    }
}

public sealed class EmbedSubtitlesAction : CombineFilesAction<MediaOptions>
{
    private readonly CompositeMediaEngine _engine;
    private readonly FileInputFilter _input;

    public EmbedSubtitlesAction(CompositeMediaEngine engine, FileActionServices services) : base(services)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _input = new FileInputFilter(SR.GetString("MediaFiles"), MediaAction.VideoExtensions.Concat(MediaAction.SubtitleExtensions).ToArray(), MediaAction.VideoFormats);

        Descriptor = new ActionDescriptor(
            "powertrove.video.embed-subtitles",
            SR.GetString("MediaActions_Title_EmbedSubtitles"),
            SR.GetString("MediaActions_Desc_EmbedSubtitles"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "video", "subtitle", "embed", "track" },
            RequiresPreview: true, ChangesFiles: true,
            Selection: SelectionRequirements.Any("video", "subtitle") with
            {
                MinItems = 2,
                AllowOtherItems = false,
                FamilyLimits = new Dictionary<string, FamilyLimit>
                {
                    { "video", new FamilyLimit(1, 1) },
                    { "subtitle", new FamilyLimit(1, 3) }
                }
            });
    }

    public override ActionDescriptor Descriptor { get; }

    protected override FileInputFilter Input => _input;

    protected override string? UnavailableReason => _engine.CanProduce(MediaOperation.EmbedSubtitles, MediaTargets.SameAsSource, MediaAction.VideoExtensions)
        ? null : SR.GetString("Media_RequiresFFmpeg");

    protected override MediaOptions ReadOptions(ActionParameterValues values) => new();

    protected override string DescribeOperation(int inputCount, MediaOptions options) => SR.GetString("MediaActions_Operation_EmbedSubtitles");

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, MediaOptions options)
    {
        var video = inputs.First(i => SelectionFamilies.Of(i).Equals("video", StringComparison.OrdinalIgnoreCase));
        string baseName = Path.GetFileNameWithoutExtension(video.Name);
        return new PlannedOutput(baseName + " (subtitled)", Path.GetExtension(video.Name).ToLowerInvariant());
    }

    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, MediaOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var byFamily = ByFamily(items);
        var videoItem = byFamily["video"].First();
        var subtitleItems = byFamily["subtitle"].OrderBy(i => i.BaseName, NaturalFileNameComparer.Instance).ToList();

        string sourceExtension = Path.GetExtension(videoItem.Path).ToLowerInvariant();
        var subtitlePaths = subtitleItems.Select(i => i.Path).ToArray();
        var request = new MediaRequest(MediaOperation.EmbedSubtitles, MediaTargets.SameAsSource, sourceExtension, subtitlePaths);

        var engine = _engine.Select(request) ?? throw MediaMessages.Failure("MediaNoEngine");

        await output.WriteFileFromPathAsync(videoItem.Directory, videoItem.BaseName + " (subtitled)", sourceExtension,
            (destination, ct) => MediaMessages.RunAsync(() => engine.ProcessAsync(videoItem.Path, destination, request, ct), ct), cancellationToken).ConfigureAwait(false);
    }
}

public sealed class StillImageVideoAction : CombineFilesAction<MediaOptions>
{
    private readonly CompositeMediaEngine _engine;
    private readonly FileInputFilter _input;

    public StillImageVideoAction(CompositeMediaEngine engine, FileActionServices services) : base(services)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _input = new FileInputFilter(SR.GetString("MediaFiles"), new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".avif", ".heic", ".heif" }.Concat(MediaAction.AudioExtensions).ToArray(), new[] { FileFormats.Jpeg, FileFormats.Png, FileFormats.Gif, FileFormats.Bmp, FileFormats.Tiff, FileFormats.WebP, FileFormats.Avif, FileFormats.Heic }.Concat(MediaAction.AudioFormats).ToArray());

        Descriptor = new ActionDescriptor(
            "powertrove.video.still-image",
            SR.GetString("MediaActions_Title_StillImageVideo"),
            SR.GetString("MediaActions_Desc_StillImageVideo"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "video", "image", "audio", "create" },
            RequiresPreview: true, ChangesFiles: true,
            Selection: SelectionRequirements.Multiple("image", "audio") with
            {
                AllowOtherItems = false,
                FamilyLimits = new Dictionary<string, FamilyLimit>
                {
                    { "image", new FamilyLimit(1, 1) },
                    { "audio", new FamilyLimit(1, 1) }
                }
            });
    }

    public override ActionDescriptor Descriptor { get; }

    protected override FileInputFilter Input => _input;

    protected override string? UnavailableReason => _engine.CanProduce(MediaOperation.StillImageVideo, MediaTargets.Mp4, new[] { ".jpg" })
        ? null : SR.GetString("Media_RequiresFFmpeg");

    protected override MediaOptions ReadOptions(ActionParameterValues values) => new();

    protected override string DescribeOperation(int inputCount, MediaOptions options) => SR.GetString("MediaActions_Operation_StillImageVideo");

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, MediaOptions options)
    {
        var audio = inputs.First(i => MediaAction.AudioExtensions.Contains(Path.GetExtension(i.Name).ToLowerInvariant()));
        string baseName = Path.GetFileNameWithoutExtension(audio.Name);
        return new PlannedOutput(baseName + " (video)", ".mp4");
    }

    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, MediaOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var byFamily = ByFamily(items);
        var imageItem = byFamily["image"].First();
        var audioItem = byFamily["audio"].First();

        string sourceExtension = Path.GetExtension(imageItem.Path).ToLowerInvariant();
        var request = new MediaRequest(MediaOperation.StillImageVideo, MediaTargets.Mp4, sourceExtension, new[] { audioItem.Path });

        var engine = _engine.Select(request) ?? throw MediaMessages.Failure("MediaNoEngine");

        await output.WriteFileFromPathAsync(imageItem.Directory, audioItem.BaseName + " (video)", ".mp4",
            (destination, ct) => MediaMessages.RunAsync(() => engine.ProcessAsync(imageItem.Path, destination, request, ct), ct), cancellationToken).ConfigureAwait(false);
    }
}


public sealed class SlideshowVideoAction : CombineFilesAction<MediaOptions>
{
    private readonly CompositeMediaEngine _engine;
    private readonly FileInputFilter _input;

    public SlideshowVideoAction(CompositeMediaEngine engine, FileActionServices services) : base(services)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _input = new FileInputFilter(SR.GetString("MediaFiles"), new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".avif", ".heic", ".heif" }.Concat(MediaAction.AudioExtensions).ToArray(), new[] { FileFormats.Jpeg, FileFormats.Png, FileFormats.Gif, FileFormats.Bmp, FileFormats.Tiff, FileFormats.WebP, FileFormats.Avif, FileFormats.Heic }.Concat(MediaAction.AudioFormats).ToArray());

        var secondsChoices = new[] { new ActionParameterChoice("auto", SR.GetString("MediaChoiceauto")) }.Concat(Enumerable.Range(1, 30).Select(i => new ActionParameterChoice(i.ToString(CultureInfo.InvariantCulture), SR.Count("MediaSeconds", i)))).ToArray();
        var sizeChoices = new[] { "1080p", "720p", "square" }.Select(c => new ActionParameterChoice(c, SR.GetString("MediaChoice" + c))).ToArray();

        Descriptor = new ActionDescriptor(
            "powertrove.video.slideshow",
            SR.GetString("MediaActions_Title_SlideshowVideo"),
            SR.GetString("MediaActions_Desc_SlideshowVideo"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "video", "image", "audio", "create", "slideshow" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("SecondsPerImage", SR.GetString("MediaParamSecondsPerImage"), "3", secondsChoices),
                ActionParameterDefinition.Choice("Size", SR.GetString("MediaParamSize"), "1080p", sizeChoices)
            },
            Selection: new SelectionRequirements(new HashSet<string> { "image", "audio" }, MinItems: 2, MaxItems: 301, AllowOtherItems: false, FamilyLimits: new Dictionary<string, FamilyLimit>
            {
                { "image", new FamilyLimit(2, 300) },
                { "audio", new FamilyLimit(0, 1) }
            }));
    }

    public override ActionDescriptor Descriptor { get; }

    protected override FileInputFilter Input => _input;

    protected override string? UnavailableReason => _engine.CanProduce(MediaOperation.Slideshow, MediaTargets.Mp4, new[] { ".jpg" })
        ? null : SR.GetString("Media_RequiresFFmpeg");

    protected override MediaOptions ReadOptions(ActionParameterValues values) => new(
        values.GetChoice("SecondsPerImage"),
        values.GetChoice("Size")
    );

    protected override string DescribeOperation(int inputCount, MediaOptions options) => SR.GetString("MediaActions_Operation_SlideshowVideo");

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, MediaOptions options)
    {
        var image = inputs.First(i => !MediaAction.AudioExtensions.Contains(Path.GetExtension(i.Name).ToLowerInvariant()));
        string baseName = Path.GetFileNameWithoutExtension(image.Name);
        return new PlannedOutput(baseName + " (slideshow)", ".mp4");
    }

    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, MediaOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var byFamily = ByFamily(items);
        var imageItems = byFamily["image"].OrderBy(i => i.BaseName, NaturalFileNameComparer.Instance).ToList();
        var audioItems = byFamily.Contains("audio") ? byFamily["audio"].ToList() : new List<FileWorkItem>();

        var imageItem = imageItems.First();
        var audioItem = audioItems.FirstOrDefault();

        string sourceExtension = Path.GetExtension(imageItem.Path).ToLowerInvariant();

        var requestOptions = new Dictionary<string, string>
        {
            { "SecondsPerImage", options.SecondsPerImage },
            { "Size", options.Size }
        };

        var additionalInputs = imageItems.Skip(1).Select(i => i.Path).ToList();
        if (audioItem != null) additionalInputs.Add(audioItem.Path);

        var request = new MediaRequest(MediaOperation.Slideshow, MediaTargets.Mp4, sourceExtension, additionalInputs, requestOptions);

        var engine = _engine.Select(request) ?? throw MediaMessages.Failure("MediaNoEngine");

        await output.WriteFileFromPathAsync(imageItem.Directory, imageItem.BaseName + " (slideshow)", ".mp4",
            (destination, ct) => MediaMessages.RunAsync(() => engine.ProcessAsync(imageItem.Path, destination, request, ct), ct), cancellationToken).ConfigureAwait(false);
    }
}
