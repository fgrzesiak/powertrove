using System.Globalization;

namespace ContextBar.Pack.Media;

public sealed record MediaToolOptions(string Start, string End, bool Precise, string Quality, bool KeepAudio,
    int Duration, int Fps, int Width, string Mode, string At, string EverySeconds, string Format, string Angle, bool MetadataOnly);

public sealed class MediaToolAction : PerFileAction<MediaToolOptions>
{
    private readonly string _kind;
    private readonly CompositeMediaEngine _engine;
    private readonly IMediaInspector _inspector;
    public MediaToolAction(string kind, CompositeMediaEngine engine, IMediaInspector inspector, FileActionServices services) : base(services)
    { _kind = kind; _engine = engine; _inspector = inspector; }
    private MediaOperation Operation => _kind switch { "trim" => MediaOperation.Trim, "compress" => MediaOperation.CompressVideo, "to-gif" => MediaOperation.ToGif, "extract-frames" => MediaOperation.ExtractFrames, "normalize" => MediaOperation.NormalizeAudio, _ => MediaOperation.RotateVideo };
    private string Target => _kind switch { "compress" or "rotate" => MediaTargets.Mp4, "to-gif" => MediaTargets.Gif, "extract-frames" => MediaTargets.Frames, _ => MediaTargets.SameAsSource };
    private bool NeedsInspection => _kind is not ("normalize" or "rotate");
    private static ActionParameterDefinition Text(string key, string value) => ActionParameterDefinition.Text(key, SR.GetString("MediaParam" + key), value);
    private static ActionParameterDefinition Bool(string key, bool value) => ActionParameterDefinition.Boolean(key, SR.GetString("MediaParam" + key), value);
    private static ActionParameterDefinition Choice(string key, string value, params string[] choices) => ActionParameterDefinition.Choice(key, SR.GetString("MediaParam" + key), value, choices.Select(c => new ActionParameterChoice(c, SR.GetString("MediaChoice" + c))).ToArray());
    public override ActionDescriptor Descriptor => new("contextbar." + (_kind == "trim" ? "media" : _kind == "normalize" ? "audio" : "video") + "." + _kind,
        SR.GetString("MediaTitle" + _kind), SR.GetString("MediaDesc" + _kind), new HashSet<ContentKind> { ContentKind.Files }, ["media", "video", "audio", _kind], true, true,
        Parameters: _kind switch
        {
            "trim" => [Text("Start", "0"), Text("End", ""), Bool("Precise", false)],
            "compress" => [Choice("Quality", "medium", "high", "medium", "small"), Bool("KeepAudio", true)],
            "to-gif" => [Text("Start", "0"), ActionParameterDefinition.Integer("Duration", SR.GetString("MediaParamDuration"), 5, 1, 30), ActionParameterDefinition.Integer("Fps", SR.GetString("MediaParamFps"), 12, 5, 30), ActionParameterDefinition.Integer("Width", SR.GetString("MediaParamWidth"), 480, 100, 1000)],
            "extract-frames" => [Choice("Mode", "single", "single", "interval"), Text("At", "0").VisibleWhen("Mode", "single"), Text("EverySeconds", "1").VisibleWhen("Mode", "interval"), Choice("Format", "jpg", "jpg", "png")],
            "rotate" => [Choice("Angle", "90", "90", "180", "270"), Bool("MetadataOnly", false)], _ => []
        }, Selection: SelectionRequirements.Any(_kind == "trim" ? ["audio", "video"] : _kind == "normalize" ? ["audio"] : ["video"]));
    protected override FileInputFilter Input => _kind == "trim" ? new(SR.GetString("MediaFiles"), MediaAction.AudioExtensions.Concat(MediaAction.VideoExtensions), MediaAction.AudioFormats.Concat(MediaAction.VideoFormats))
        : _kind == "normalize" ? new(SR.GetString("MediaAudioFiles"), MediaAction.AudioExtensions, MediaAction.AudioFormats) : new(SR.GetString("MediaVideoFiles"), MediaAction.VideoExtensions, MediaAction.VideoFormats);
    protected override string? UnavailableReason => NeedsInspection && !_inspector.IsAvailable ? SR.GetString("MediaInspectUnavailable") : !_engine.CanProduce(Operation, Target, Input.Extensions!) ? SR.GetString(_kind is "normalize" or "rotate" ? "Media_RequiresFFmpeg" : "MediaNoEngine") : null;
    protected override MediaToolOptions ReadOptions(ActionParameterValues v) => new(_kind is "trim" or "to-gif" ? v.GetText("Start") : "0", _kind == "trim" ? v.GetText("End") : "", _kind == "trim" && v.GetBoolean("Precise"), _kind == "compress" ? v.GetChoice("Quality") : "medium", _kind != "compress" || v.GetBoolean("KeepAudio"),
        _kind == "to-gif" ? v.GetInt32("Duration") : 5, _kind == "to-gif" ? v.GetInt32("Fps") : 12, _kind == "to-gif" ? v.GetInt32("Width") : 480,
        _kind == "extract-frames" ? v.GetChoice("Mode") : "single", _kind == "extract-frames" ? v.GetText("At") : "0", _kind == "extract-frames" ? v.GetText("EverySeconds") : "1", _kind == "extract-frames" ? v.GetChoice("Format") : "jpg", _kind == "rotate" ? v.GetChoice("Angle") : "90", _kind == "rotate" && v.GetBoolean("MetadataOnly"));
    protected override string? ValidateOptions(MediaToolOptions o)
    {
        if (!MediaTime.TryParse(o.Start, out var start) || o.End.Length > 0 && (!MediaTime.TryParse(o.End, out var end) || end <= start)) return SR.GetString("MediaInvalidRange");
        if (_kind == "extract-frames" && (o.Mode == "single" ? !MediaTime.TryParse(o.At, out _) : !MediaTime.TryParse(o.EverySeconds, out var interval) || interval < TimeSpan.FromSeconds(.5) || interval > TimeSpan.FromHours(1))) return SR.GetString("MediaInvalidTime");
        return null;
    }
    protected override string DescribeOperation(int count, MediaToolOptions o) => SR.GetString("MediaDesc" + _kind);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, MediaToolOptions o) => _kind switch
    {
        "trim" => [SR.GetString("MediaTrimAccuracy")], "extract-frames" when o.Mode == "interval" => [SR.GetString("MediaFrameCap")],
        "rotate" when o.MetadataOnly => [SR.GetString("MediaRotationWarning")], _ => []
    };
    private string Extension(SelectedFile f, MediaToolOptions o) => _kind == "rotate" && o.MetadataOnly ? f.Extension.ToLowerInvariant() : Target == MediaTargets.SameAsSource ? f.Extension.ToLowerInvariant() : "." + Target;
    private string Stem(string source) => source + (_kind switch { "trim" => " (trimmed)", "compress" => " (compressed)", "extract-frames" => " (frames)", "normalize" => " (normalized)", "rotate" => " (rotated)", _ => "" });
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile f, MediaToolOptions o) { yield return new(Stem(Path.GetFileNameWithoutExtension(f.Name)), _kind == "extract-frames" ? "" : Extension(f, o), _kind == "extract-frames"); }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot c, ActionParameters p, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(c, p, ct).ConfigureAwait(false);
        if (preview.IsBlocked) return preview;
        var options = ReadOptions(ActionParameterValues.From(p, Descriptor.Parameters));
        if (_kind == "rotate" && options.MetadataOnly && ((FileSelection)c.Content).Files.Where(Input.Accepts).Any(f => f.Extension.ToLowerInvariant() is not (".mp4" or ".mov" or ".m4v")))
            return preview with { IsBlocked = true, PreviewText = SR.GetString("MediaMetadataContainer") };
        if (_kind != "to-gif") return preview;
        try
        {
            foreach (var file in ((FileSelection)c.Content).Files.Where(Input.Accepts))
            { var info = await MediaMessages.InspectAsync(_inspector, file.Path!, ct).ConfigureAwait(false); ValidateDetails(info, options); }
            return preview;
        }
        catch (Exception ex) when (ex is FileProcessingException or ArgumentException or IOException)
        { return preview with { IsBlocked = true, PreviewText = MediaMessages.PreviewError(ex) }; }
    }
    private void ValidateDetails(MediaDetails info, MediaToolOptions o)
    {
        MediaTime.TryParse(o.Start, out var start);
        if (_kind == "trim" && (start >= info.Duration || o.End.Length > 0 && MediaTime.TryParse(o.End, out var end) && end > info.Duration)) throw MediaMessages.Failure("MediaRangeBeyondDuration");
        if ((_kind is "compress" or "to-gif" or "extract-frames") && !info.HasVideo) throw MediaMessages.Failure("MediaNoVideo");
        if (_kind == "extract-frames" && o.Mode == "single" && MediaTime.TryParse(o.At, out var at) && at >= info.Duration)
            throw MediaMessages.Failure("MediaRangeBeyondDuration");
        if (_kind == "to-gif")
        {
            if (start >= info.Duration) throw MediaMessages.Failure("MediaRangeBeyondDuration");
            MediaPlanning.GifTimes(start, TimeSpan.FromSeconds(o.Duration), o.Fps, info.Duration);
            MediaPlanning.GifSize(info.Width ?? 0, info.Height ?? 0, o.Width);
        }
    }
    protected override bool ReportSizeChange => _kind == "compress";
    protected override async Task ProcessAsync(FileWorkItem item, MediaToolOptions o, FileOutputWriter output, CancellationToken ct)
    {
        if (_kind == "rotate" && o.MetadataOnly && item.File.Extension.ToLowerInvariant() is not (".mp4" or ".mov" or ".m4v"))
            throw MediaMessages.Failure("MediaMetadataContainer");
        MediaDetails? info = NeedsInspection ? await MediaMessages.InspectAsync(_inspector, item.Path, ct).ConfigureAwait(false) : null;
        if (info is not null) ValidateDetails(info, o);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Start"] = o.Start, ["End"] = o.End, ["Precise"] = o.Precise.ToString(), ["Quality"] = o.Quality, ["KeepAudio"] = o.KeepAudio.ToString(),
            ["Duration"] = o.Duration.ToString(CultureInfo.InvariantCulture), ["Fps"] = o.Fps.ToString(CultureInfo.InvariantCulture), ["Width"] = o.Width.ToString(CultureInfo.InvariantCulture),
            ["Mode"] = o.Mode, ["At"] = o.At, ["EverySeconds"] = o.EverySeconds, ["Format"] = o.Format, ["Angle"] = o.Angle, ["MetadataOnly"] = o.MetadataOnly.ToString(), ["Stem"] = item.BaseName
        };
        var request = new MediaRequest(Operation, _kind == "rotate" && o.MetadataOnly ? MediaTargets.SameAsSource : Target, item.File.Extension.ToLowerInvariant(), Options: values, InputDetails: info is null ? null : [info]);
        if (_kind == "extract-frames")
            await output.WriteDirectoryAsync(item.Directory, Stem(item.BaseName), (folder, token) => MediaMessages.RunAsync(() => _engine.ProcessAsync(item.Path, folder, request, token), token), ct).ConfigureAwait(false);
        else
            await output.WriteFileFromPathAsync(item.Directory, Stem(item.BaseName), Extension(item.File, o), async (path, token) =>
            {
                await MediaMessages.RunAsync(() => _engine.ProcessAsync(item.Path, path, request, token), token).ConfigureAwait(false);
                if (_kind == "compress" && Services.FileSystem.GetFileLength(path) >= Services.FileSystem.GetFileLength(item.Path)) throw new FileSkippedException();
            }, ct).ConfigureAwait(false);
    }
}

public sealed class MediaInfoAction(IMediaInspector inspector, FileActionServices services) : CombineFilesAction<bool>(services)
{
    public override ActionDescriptor Descriptor => new("contextbar.media.info", SR.GetString("MediaTitleinfo"), SR.GetString("MediaDescinfo"), new HashSet<ContentKind> { ContentKind.Files }, ["media", "info", "duration", "codec", "bitrate"], false, false, Selection: SelectionRequirements.Single("audio", "video"));
    protected override int MinimumInputs => 1; protected override int MaximumInputs => 1;
    protected override bool RequiresEntireSelection => true;
    protected override string? UnavailableReason => inspector.IsAvailable ? null : SR.GetString("MediaInspectUnavailable");
    protected override FileInputFilter Input => new(SR.GetString("MediaFiles"), MediaAction.AudioExtensions.Concat(MediaAction.VideoExtensions), MediaAction.AudioFormats.Concat(MediaAction.VideoFormats));
    protected override bool ReadOptions(ActionParameterValues v) => true;
    protected override string DescribeOperation(int count, bool o) => SR.GetString("MediaDescinfo");
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, bool o) => throw new NotSupportedException();
    protected override IReadOnlyList<string> PlanExpectedOutputs(IReadOnlyList<SelectedFile> inputs, bool o) => [];
    protected override Task ProcessAsync(IReadOnlyList<FileWorkItem> items, bool o, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
    protected override async Task ProcessWithStatisticsAsync(IReadOnlyList<FileWorkItem> items, bool o, FileOutputWriter output, ExecutionStatistics stats, IProgress<double>? progress, CancellationToken ct)
    { stats.ResultMessage = MediaTechnical.Format(await MediaMessages.InspectAsync(inspector, items[0].Path, ct).ConfigureAwait(false)); stats.CompletedWithoutOutput = true; }
}

public sealed class JoinMediaAction(CompositeMediaEngine engine, IMediaInspector inspector, FileActionServices services) : CombineFilesAction<bool>(services)
{
    public override ActionDescriptor Descriptor => new("contextbar.media.merge", SR.GetString("MediaTitlemerge"), SR.GetString("MediaDescmerge"), new HashSet<ContentKind> { ContentKind.Files }, ["media", "join", "merge", "audio", "video"], true, true, Selection: new SelectionRequirements(new HashSet<string> { "audio", "video" }, MinItems: 2, MaxItems: 100, AllowOtherItems: false));
    protected override int MaximumInputs => 100;
    protected override bool RequiresEntireSelection => true;
    protected override FileInputFilter Input => new(SR.GetString("MediaFiles"), MediaAction.AudioExtensions.Concat(MediaAction.VideoExtensions), MediaAction.AudioFormats.Concat(MediaAction.VideoFormats));
    protected override string? UnavailableReason => !inspector.IsAvailable ? SR.GetString("MediaInspectUnavailable") : engine.CanProduce(MediaOperation.Merge, MediaTargets.Mp4, MediaAction.VideoExtensions) || engine.CanProduce(MediaOperation.Merge, MediaTargets.M4a, MediaAction.AudioExtensions) ? null : SR.GetString("MediaNoEngine");
    protected override string? ValidateSelection(IReadOnlyList<SelectedFile> files) => files.Select(SelectionFamilies.Of).Distinct().Count() > 1 ? SR.GetString("MediaMixedMerge") : null;
    protected override bool ReadOptions(ActionParameterValues v) => true;
    protected override string DescribeOperation(int count, bool o) => SR.GetString("MediaDescmerge");
    private static bool Video(SelectedFile file) => SelectionFamilies.Of(file) == "video";
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, bool o) => new(Path.GetFileNameWithoutExtension(inputs[0].Name) + " (joined)", Video(inputs[0]) ? ".mp4" : ".m4a");
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot c, ActionParameters p, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(c, p, ct).ConfigureAwait(false); if (preview.IsBlocked) return preview;
        try
        {
            var info = new List<MediaDetails>();
            foreach (var f in NaturalFileNameComparer.Sort(((FileSelection)c.Content).Files)) info.Add(await MediaMessages.InspectAsync(inspector, f.Path!, ct).ConfigureAwait(false));
            return info.Select(i => (i.Width, i.Height)).Distinct().Count() > 1 ? preview with { Warnings = preview.Warnings.Append(SR.GetString("MediaMergeScaleWarning")).ToArray() } : preview;
        }
        catch (Exception ex) when (ex is FileProcessingException or IOException) { return preview with { IsBlocked = true, PreviewText = SR.GetString("MediaInspectFailed") }; }
    }
    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, bool o, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct)
    {
        var info = new List<MediaDetails>(); foreach (var item in items) info.Add(await MediaMessages.InspectAsync(inspector, item.Path, ct).ConfigureAwait(false));
        bool video = Video(items[0].File);
        if (info.Any(i => i.HasVideo != video || !video && !i.HasAudio)) throw MediaMessages.Failure("MediaMixedMerge");
        var request = new MediaRequest(MediaOperation.Merge, video ? MediaTargets.Mp4 : MediaTargets.M4a, items[0].File.Extension.ToLowerInvariant(), items.Skip(1).Select(i => i.Path).ToArray(), InputDetails: info);
        await output.WriteFileFromPathAsync(items[0].Directory, items[0].BaseName + " (joined)", video ? ".mp4" : ".m4a", (path, token) => MediaMessages.RunAsync(() => engine.ProcessAsync(items[0].Path, path, request, token), token), ct).ConfigureAwait(false);
    }
}
