using PowerTrove.Abstractions.Files;
using PowerTrove.Application.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace PowerTrove.Platform.Windows.Media;

/// <summary>Media Foundation playback from a verified local stream, never a URL or playlist.</summary>
public sealed class LocalMediaPlayback(IFileTypeVerifier verifier) : ILocalMediaPreview
{
    private static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".aac"] = "audio/aac", [".ogg"] = "audio/ogg", [".flac"] = "audio/flac",
        [".m4a"] = "audio/mp4", [".wma"] = "audio/x-ms-wma", [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska", [".avi"] = "video/x-msvideo", [".wmv"] = "video/x-ms-wmv", [".webm"] = "video/webm"
    };
    private static readonly FileInputFilter Filter = new(nameof(LocalMediaPlayback), ContentTypes.Keys,
        [FileFormats.Mp3, FileFormats.Wav, FileFormats.Aac, FileFormats.Ogg, FileFormats.Flac, FileFormats.IsoMedia, FileFormats.Asf, FileFormats.Matroska, FileFormats.Avi]);
    private readonly object _gate = new();
    private FileStream? _file;
    private IRandomAccessStream? _stream;
    private MediaSource? _source;
    private MediaPreviewInfo? _info;
    private bool _disposed;
    private bool _opening;
    private MediaPlayer? _player;
    private double _volume = .3;
    private bool _muted;
    public MediaPlayer Player => _player ?? throw new MediaPreviewException(MediaPreviewFailure.Unavailable);
    public event EventHandler? Failed;
    public TimeSpan Position => _player?.PlaybackSession.Position ?? TimeSpan.Zero;
    public bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public double Volume { get => _player?.Volume ?? _volume; set { _volume = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : .3; if (_player is not null) _player.Volume = _volume; } }
    public bool IsMuted { get => _player?.IsMuted ?? _muted; set { _muted = value; if (_player is not null) _player.IsMuted = value; } }

    public Task<MediaPreviewInfo> OpenAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => OpenCoreAsync(path, cancellationToken), cancellationToken);

    private async Task<MediaPreviewInfo> OpenCoreAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            !ContentTypes.TryGetValue(Path.GetExtension(path), out var contentType)) throw new MediaPreviewException(MediaPreviewFailure.InvalidInput);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpened(MediaPlayer sender, object args) => opened.TrySetResult();
        void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => opened.TrySetException(new MediaPreviewException(MediaPreviewFailure.Unsupported));
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_opening) throw new InvalidOperationException("Preview already opened.");
                _opening = true;
                _player = new MediaPlayer { AutoPlay = false, Volume = _volume, IsMuted = _muted };
                Player.CommandManager.IsEnabled = false;
                Player.MediaOpened += OnOpened; Player.MediaFailed += OnFailed;
            }
            var detected = await verifier.VerifyAsync(path, Filter, token).ConfigureAwait(false);
            string expected = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".mp3" => FileFormats.Mp3, ".wav" => FileFormats.Wav, ".aac" => FileFormats.Aac, ".ogg" => FileFormats.Ogg,
                ".flac" => FileFormats.Flac, ".wma" or ".wmv" => FileFormats.Asf, ".mkv" or ".webm" => FileFormats.Matroska,
                ".avi" => FileFormats.Avi, _ => FileFormats.IsoMedia
            };
            if (!detected.IsAccepted || detected.DetectedFormat != expected) throw new MediaPreviewException(MediaPreviewFailure.InvalidInput);
            var file = await Task.Run(() =>
            {
                var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
                try { token.ThrowIfCancellationRequested(); return input; } catch { input.Dispose(); throw; }
            }, token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || token.IsCancellationRequested) { file.Dispose(); token.ThrowIfCancellationRequested(); throw new ObjectDisposedException(nameof(LocalMediaPlayback)); }
                _file = file; _stream = file.AsRandomAccessStream();
                _source = MediaSource.CreateFromStream(_stream, contentType); Player.Source = _source;
            }
            await opened.Task.WaitAsync(token).ConfigureAwait(false);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var session = Player.PlaybackSession;
                if (!session.CanSeek) throw new MediaPreviewException(MediaPreviewFailure.NotSeekable);
                if (session.NaturalDuration < MediaTrimRange.MinimumLength || session.NaturalDuration > TimeSpan.FromDays(7) ||
                    (long)session.NaturalVideoWidth * session.NaturalVideoHeight > 80_000_000) throw new MediaPreviewException(MediaPreviewFailure.Unsupported);
                _info = new(session.NaturalDuration, session.NaturalVideoWidth > 0 && session.NaturalVideoHeight > 0);
                Player.MediaFailed += PlaybackFailed;
                return _info;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Dispose(); throw; }
        catch (Exception ex) when (ex is not MediaPreviewException and not OutOfMemoryException)
        { Dispose(); throw new MediaPreviewException(MediaPreviewFailure.Unavailable); }
        catch { Dispose(); throw; }
        finally
        {
            lock (_gate) if (!_disposed) { Player.MediaOpened -= OnOpened; Player.MediaFailed -= OnFailed; }
        }
    }
    private void PlaybackFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => Failed?.Invoke(this, EventArgs.Empty);
    public void Play() => Player.Play();
    public void Pause() => Player.Pause();
    public void Seek(TimeSpan position)
    {
        if (_info is null) throw new MediaPreviewException(MediaPreviewFailure.Unavailable);
        Player.PlaybackSession.Position = position < TimeSpan.Zero ? TimeSpan.Zero : position > _info.Duration ? _info.Duration : position;
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            if (_player is not null)
            {
                _player.MediaFailed -= PlaybackFailed;
                Release(() => _player.Pause()); Release(() => _player.Source = null); Release(_player.Dispose);
            }
            if (_source is not null) Release(_source.Dispose);
            if (_stream is not null) Release(_stream.Dispose);
            if (_file is not null) Release(_file.Dispose);
            Failed = null;
        }
    }
    private static void Release(Action release) { try { release(); } catch (Exception ex) when (ex is not OutOfMemoryException) { } }
}
