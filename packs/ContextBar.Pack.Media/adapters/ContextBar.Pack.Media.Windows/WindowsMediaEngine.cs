using ContextBar.Abstractions.Files;
using ContextBar.Pack.Media;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace ContextBar.Pack.Media.Windows;

/// <summary>
/// Media engine on Windows Media Foundation (MediaTranscoder). Needs no installation and
/// covers the common formats; WebM output and Ogg input remain FFmpeg-only.
/// </summary>
public sealed partial class WindowsMediaEngine : IMediaEngine
{
    private static readonly HashSet<string> AudioSources = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac" };
    private static readonly HashSet<string> VideoSources = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv" };
    private static readonly HashSet<string> Mp4Sources = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v" };

    // Windows N editions (without the Media Feature Pack) and some Server installations ship
    // without Media Foundation; the engine then reports itself unavailable instead of failing.
    private static readonly Lazy<bool> MediaFoundationPresent = new(() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "mfplat.dll")) &&
        File.Exists(Path.Combine(Environment.SystemDirectory, "mfreadwrite.dll")));

    public string Name => "Windows Media Foundation";

    public bool IsAvailable => MediaFoundationPresent.Value;

    public bool Supports(MediaRequest request) => request.Operation switch
    {
        MediaOperation.ConvertAudio => AudioSources.Contains(request.SourceExtension)
            && request.Target is MediaTargets.Mp3 or MediaTargets.M4a or MediaTargets.Wav or MediaTargets.Flac,
        MediaOperation.ExtractAudio => VideoSources.Contains(request.SourceExtension) && request.Target is MediaTargets.Mp3 or MediaTargets.M4a,
        MediaOperation.ConvertVideo => VideoSources.Contains(request.SourceExtension) && request.Target == MediaTargets.Mp4,
        MediaOperation.RemoveAudio => Mp4Sources.Contains(request.SourceExtension),
        MediaOperation.ReplaceAudio or MediaOperation.StillImageVideo or MediaOperation.EmbedSubtitles or MediaOperation.Slideshow => false,
        MediaOperation.Trim => request.Target == MediaTargets.SameAsSource && new[] { ".mp4", ".m4v", ".wmv", ".mp3", ".m4a", ".wav", ".flac", ".wma" }.Contains(request.SourceExtension, StringComparer.OrdinalIgnoreCase),
        MediaOperation.CompressVideo => VideoSources.Contains(request.SourceExtension) && request.Target == MediaTargets.Mp4,
        MediaOperation.Merge => request.Target == MediaTargets.Mp4 && VideoSources.Contains(request.SourceExtension) || request.Target == MediaTargets.M4a && AudioSources.Contains(request.SourceExtension),
        MediaOperation.ToGif => VideoSources.Contains(request.SourceExtension) && request.Target == MediaTargets.Gif,
        MediaOperation.ExtractFrames => VideoSources.Contains(request.SourceExtension) && request.Target == MediaTargets.Frames,
        _ => false
    };

    public async Task ProcessAsync(string sourcePath, string destinationPath, MediaRequest request, CancellationToken cancellationToken)
    {
        if (request.Operation is MediaOperation.Trim or MediaOperation.CompressVideo or MediaOperation.Merge or MediaOperation.ToGif or MediaOperation.ExtractFrames)
        { await ProcessAdvancedAsync(sourcePath, destinationPath, request, cancellationToken).ConfigureAwait(false); return; }
        // MediaTranscoder writes into an existing StorageFile; the caller owns cleanup on failure.
        await using (File.Create(destinationPath)) { }

        PrepareTranscodeResult prepared;
        try
        {
            var input = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(cancellationToken).ConfigureAwait(false);
            var output = await StorageFile.GetFileFromPathAsync(destinationPath).AsTask(cancellationToken).ConfigureAwait(false);
            var profile = await CreateProfileAsync(input, request).ConfigureAwait(false);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = false };
            prepared = await transcoder.PrepareFileTranscodeAsync(input, output, profile).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FileProcessingException)
        {
            throw new FileProcessingException(ContextBar.Pack.Media.Windows.Resources.Strings.MediaOpenFailed, ex);
        }

        if (!prepared.CanTranscode)
        {
            throw new FileProcessingException(prepared.FailureReason switch
            {
                TranscodeFailureReason.CodecNotFound => ContextBar.Pack.Media.Windows.Resources.Strings.MediaCodecNotFound,
                TranscodeFailureReason.InvalidProfile => ContextBar.Pack.Media.Windows.Resources.Strings.MediaInvalidProfile,
                _ => ContextBar.Pack.Media.Windows.Resources.Strings.MediaConversionNotSupported
            });
        }

        try
        {
            await prepared.TranscodeAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileProcessingException(ContextBar.Pack.Media.Windows.Resources.Strings.MediaConversionFailed, ex);
        }
    }

    private static async Task<MediaEncodingProfile> CreateProfileAsync(StorageFile input, MediaRequest request)
    {
        switch (request.Operation)
        {
            case MediaOperation.ConvertAudio:
            case MediaOperation.ExtractAudio:
            {
                var profile = request.Target switch
                {
                    MediaTargets.Mp3 => MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High),
                    MediaTargets.M4a => MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High),
                    MediaTargets.Wav => MediaEncodingProfile.CreateWav(AudioEncodingQuality.High),
                    MediaTargets.Flac => MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High),
                    _ => throw new FileProcessingException(ContextBar.Pack.Media.Windows.Resources.Strings.MediaUnsupportedAudio)
                };
                profile.Video = null;
                return profile;
            }
            case MediaOperation.ConvertVideo:
                return MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            case MediaOperation.RemoveAudio:
            {
                var profile = await MediaEncodingProfile.CreateFromFileAsync(input);
                if (profile.Video is null)
                    throw new FileProcessingException(ContextBar.Pack.Media.Windows.Resources.Strings.MediaNoVideoTrack);
                profile.Audio = null;
                return profile;
            }
            default:
                throw new FileProcessingException(ContextBar.Pack.Media.Windows.Resources.Strings.MediaUnsupportedOperation);
        }
    }
}
