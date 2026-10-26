namespace ContextBar.Pack.Media;

/// <summary>
/// Optional engine using a user-installed FFmpeg (never bundled, see the licensing audit).
/// Only local files are read (<c>-protocol_whitelist file</c>) and nothing is overwritten (<c>-n</c>).
/// Raw FFmpeg output is never shown to users because it can contain paths and metadata.
/// </summary>
public sealed partial class FFmpegMediaEngine : IMediaEngine
{
    private readonly IFFmpegService _ffmpeg;

    public FFmpegMediaEngine(IFFmpegService ffmpeg)
    {
        _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
    }

    public string Name => "FFmpeg";

    public bool IsAvailable => _ffmpeg.IsAvailable;

    public bool Supports(MediaRequest request) => request.Operation switch
    {
        MediaOperation.ConvertAudio => request.Target is MediaTargets.Mp3 or MediaTargets.Flac or MediaTargets.Wav or MediaTargets.M4a,
        MediaOperation.ExtractAudio => request.Target is MediaTargets.Mp3 or MediaTargets.M4a,
        MediaOperation.ConvertVideo => request.Target is MediaTargets.Mp4 or MediaTargets.WebM,
        MediaOperation.RemoveAudio => request.Target == MediaTargets.SameAsSource,
        MediaOperation.ReplaceAudio => request.Target == MediaTargets.SameAsSource,
        MediaOperation.EmbedSubtitles => request.Target == MediaTargets.SameAsSource,
        MediaOperation.Trim or MediaOperation.NormalizeAudio => request.Target == MediaTargets.SameAsSource,
        MediaOperation.CompressVideo => request.Target == MediaTargets.Mp4,
        MediaOperation.RotateVideo => request.Target is MediaTargets.Mp4 or MediaTargets.SameAsSource,
        MediaOperation.Merge => request.Target is MediaTargets.Mp4 or MediaTargets.M4a,
        MediaOperation.ToGif => request.Target == MediaTargets.Gif,
        MediaOperation.ExtractFrames => request.Target == MediaTargets.Frames,
        MediaOperation.StillImageVideo => request.Target == MediaTargets.Mp4,
        _ => false
    };

    public async Task ProcessAsync(string sourcePath, string destinationPath, MediaRequest request, CancellationToken cancellationToken)
    {
        if (request.Operation is MediaOperation.Trim or MediaOperation.CompressVideo or MediaOperation.Merge or MediaOperation.ToGif or MediaOperation.ExtractFrames or MediaOperation.NormalizeAudio or MediaOperation.RotateVideo)
        { await ProcessAdvancedAsync(sourcePath, destinationPath, request, cancellationToken).ConfigureAwait(false); return; }
        var args = new List<string> { "-hide_banner", "-nostdin", "-n", "-protocol_whitelist", "file" };
        if (request.Operation == MediaOperation.StillImageVideo)
        {
            args.Add("-loop");
            args.Add("1");
        }
        args.Add("-i");
        args.Add(sourcePath);
        if (request.AdditionalInputs != null)
        {
            foreach (var input in request.AdditionalInputs)
            {
                args.Add("-i");
                args.Add(input);
            }
        }
        args.AddRange(CodecArguments(request));
        args.Add(destinationPath);

        var result = await _ffmpeg.ExecuteAsync(args, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Success)
        {
            string message = result.Error.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase)
                || result.Error.Contains("matches no streams", StringComparison.OrdinalIgnoreCase)
                ? "MediaNoUsableTracks"
                : "MediaProcessingFailed";
            throw MediaMessages.Failure(message);
        }
    }

    public static IEnumerable<string> CodecArguments(MediaRequest request) => (request.Operation, request.Target) switch
    {
        (MediaOperation.ConvertAudio or MediaOperation.ExtractAudio, MediaTargets.Mp3) => new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" },
        (MediaOperation.ConvertAudio or MediaOperation.ExtractAudio, MediaTargets.M4a) => new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
        (MediaOperation.ConvertAudio, MediaTargets.Flac) => new[] { "-vn", "-c:a", "flac" },
        (MediaOperation.ConvertAudio, MediaTargets.Wav) => new[] { "-vn", "-c:a", "pcm_s16le" },
        (MediaOperation.ConvertVideo, MediaTargets.Mp4) => new[] { "-c:v", "libx264", "-preset", "fast", "-crf", "23", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart" },
        (MediaOperation.ConvertVideo, MediaTargets.WebM) => new[] { "-c:v", "libvpx-vp9", "-crf", "30", "-b:v", "0", "-c:a", "libopus", "-b:a", "128k" },
        (MediaOperation.RemoveAudio, _) => new[] { "-map", "0", "-map", "-0:a", "-c", "copy" },
        (MediaOperation.ReplaceAudio, _) => request.SourceExtension is ".mp4" or ".mov" ? new[] { "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-shortest" } : new[] { "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "copy", "-shortest" },
        (MediaOperation.EmbedSubtitles, _) => GetEmbedSubtitlesArguments(request),
        (MediaOperation.StillImageVideo, _) => new[] { "-c:v", "libx264", "-tune", "stillimage", "-pix_fmt", "yuv420p", "-vf", "scale='min(iw,1920)':'min(ih,1920)':force_original_aspect_ratio=decrease,pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:a", "aac", "-shortest" },
        _ => throw MediaMessages.Failure("MediaFfmpegUnsupported")
    };

    private static IEnumerable<string> GetEmbedSubtitlesArguments(MediaRequest request)
    {
        var args = new List<string> { "-map", "0" };
        var inputs = request.AdditionalInputs ?? Array.Empty<string>();
        for (int i = 0; i < inputs.Count; i++)
        {
            args.Add("-map");
            args.Add((i + 1).ToString());
        }

        args.Add("-c");
        args.Add("copy");

        if (request.SourceExtension is ".mp4" or ".mov")
        {
            args.Add("-c:s");
            args.Add("mov_text");
        }

        for (int i = 0; i < inputs.Count; i++)
        {
            string baseName = Path.GetFileNameWithoutExtension(inputs[i]);
            string lang = Path.GetExtension(baseName).TrimStart('.');
            if (lang.Length == 2 || lang.Length == 3) // simple heuristic for valid language code like 'de' or 'ger'
            {
                args.Add($"-metadata:s:s:{i}");
                args.Add($"language={lang}");
            }
        }

        return args;
    }
}
