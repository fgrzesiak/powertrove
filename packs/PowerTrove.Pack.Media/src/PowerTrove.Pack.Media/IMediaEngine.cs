namespace PowerTrove.Pack.Media;

public enum MediaOperation
{
    ConvertAudio,
    ConvertVideo,
    ExtractAudio,
    RemoveAudio,
    ReplaceAudio,
    StillImageVideo,
    EmbedSubtitles,
    Slideshow,
    Trim,
    CompressVideo,
    Merge,
    ToGif,
    ExtractFrames,
    NormalizeAudio,
    RotateVideo
}

/// <summary>Output container/codec families produced by media actions.</summary>
public static class MediaTargets
{
    public const string Mp3 = "mp3";
    public const string M4a = "m4a";
    public const string Flac = "flac";
    public const string Wav = "wav";
    public const string Mp4 = "mp4";
    public const string WebM = "webm";
    public const string Gif = "gif";
    public const string Frames = "frames";
    /// <summary>Same container as the source.</summary>
    public const string SameAsSource = "same";
}

/// <param name="SourceExtension">Lower-case extension of the input, e.g. ".mov".</param>
public sealed record MediaRequest(MediaOperation Operation, string Target, string SourceExtension, IReadOnlyList<string>? AdditionalInputs = null,
    IReadOnlyDictionary<string, string>? Options = null, IReadOnlyList<MediaDetails>? InputDetails = null);

/// <summary>
/// Port for audio/video processing. Implementations: Windows Media Foundation
/// (OS-provided, default) and an optional user-installed FFmpeg.
/// Engines write to <c>destinationPath</c>, which does not exist yet.
/// </summary>
public interface IMediaEngine
{
    string Name { get; }

    bool IsAvailable { get; }

    bool Supports(MediaRequest request);

    Task ProcessAsync(string sourcePath, string destinationPath, MediaRequest request, CancellationToken cancellationToken);
}

/// <summary>Chooses the first available engine supporting a request (order = preference).</summary>
public sealed class CompositeMediaEngine
{
    private readonly IReadOnlyList<IMediaEngine> _engines;

    public CompositeMediaEngine(IEnumerable<IMediaEngine> engines)
    {
        _engines = engines?.ToList() ?? throw new ArgumentNullException(nameof(engines));
    }

    public bool CanProduce(MediaOperation operation, string target, IEnumerable<string> sourceExtensions) =>
        sourceExtensions.Any(ext => Select(new MediaRequest(operation, target, ext)) is not null);

    public async Task ProcessAsync(string source, string destination, MediaRequest request, CancellationToken ct)
    {
        foreach (var engine in _engines.Where(e => e.IsAvailable && e.Supports(request)))
        {
            ct.ThrowIfCancellationRequested();
            try { await engine.ProcessAsync(source, destination, request, ct).ConfigureAwait(false); return; }
            catch (MediaEngineUnsupportedException)
            {
                // Destination is owned by FileOutputWriter. A rejected native codec may
                // already have created an empty file; remove it before the next engine.
                if (File.Exists(destination)) File.Delete(destination);
                if (request.Operation == MediaOperation.ExtractFrames && Directory.Exists(destination))
                { Directory.Delete(destination, true); Directory.CreateDirectory(destination); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (FileSkippedException) { throw; }
            catch (Exception ex) { throw MediaMessages.FromEngine(ex); }
        }
        throw MediaMessages.Failure("MediaNoEngine");
    }

    public IMediaEngine? Select(MediaRequest request) => _engines.FirstOrDefault(e => e.IsAvailable && e.Supports(request));
}
