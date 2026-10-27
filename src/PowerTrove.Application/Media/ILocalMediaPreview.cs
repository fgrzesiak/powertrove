namespace PowerTrove.Application.Media;

public sealed record MediaPreviewInfo(TimeSpan Duration, bool HasVideo);
public enum MediaPreviewFailure { Unavailable, Unsupported, InvalidInput, NotSeekable }
public sealed class MediaPreviewException(MediaPreviewFailure failure) : Exception("Local media preview failed.")
{
    public MediaPreviewFailure Failure { get; } = failure;
}

/// <summary>Local, read-only playback. Implementations own and release all decoder/file resources.</summary>
public interface ILocalMediaPreview : IDisposable
{
    event EventHandler? Failed;
    Task<MediaPreviewInfo> OpenAsync(string path, CancellationToken cancellationToken);
    TimeSpan Position { get; }
    bool IsPlaying { get; }
    double Volume { get; set; }
    bool IsMuted { get; set; }
    void Play();
    void Pause();
    void Seek(TimeSpan position);
}
