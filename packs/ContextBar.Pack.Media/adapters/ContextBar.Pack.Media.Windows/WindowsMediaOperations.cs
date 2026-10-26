using System.Runtime.CompilerServices;
using ContextBar.Pack.Media;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace ContextBar.Pack.Media.Windows;

public sealed partial class WindowsMediaEngine
{
    private readonly IGifFrameEncoder _gifEncoder;
    public WindowsMediaEngine(IGifFrameEncoder? gifEncoder = null) => _gifEncoder = gifEncoder ?? new WindowsMediaGifEncoder();
    private static string Option(MediaRequest r, string key, string fallback = "") => r.Options?.TryGetValue(key, out var value) == true ? value : fallback;
    private static bool Flag(MediaRequest r, string key, bool fallback = false) => bool.TryParse(Option(r, key), out bool value) ? value : fallback;
    private static int Int(MediaRequest r, string key, int fallback) => int.TryParse(Option(r, key), out int n) ? n : fallback;
    private static TimeSpan Time(MediaRequest r, string key, string fallback = "0") => MediaTime.TryParse(Option(r, key, fallback), out var time) ? time : throw MediaMessages.ProcessingFailed();
    private static MediaDetails Details(MediaRequest r) => r.InputDetails is { Count: > 0 } ? r.InputDetails[0] : throw MediaMessages.InspectFailed();
    private async Task ProcessAdvancedAsync(string source, string destination, MediaRequest request, CancellationToken ct)
    {
        try
        {
            var input = await StorageFile.GetFileFromPathAsync(source).AsTask(ct).ConfigureAwait(false);
            if (request.Operation is MediaOperation.ExtractFrames or MediaOperation.ToGif)
            { await ExtractAsync(input, destination, request, ct).ConfigureAwait(false); return; }
            if (request.Operation == MediaOperation.Merge)
            { await MergeAsync(input, destination, request, ct).ConfigureAwait(false); return; }
            await using (File.Create(destination)) { }
            var output = await StorageFile.GetFileFromPathAsync(destination).AsTask(ct).ConfigureAwait(false);
            if (request.Operation == MediaOperation.Trim && Details(request).HasVideo)
            {
                var composition = new MediaComposition();
                var clip = await MediaClip.CreateFromFileAsync(input).AsTask(ct).ConfigureAwait(false);
                clip.TrimTimeFromStart = Time(request, "Start");
                if (Option(request, "End").Length > 0) clip.TrimTimeFromEnd = clip.OriginalDuration - Time(request, "End");
                composition.Clips.Add(clip);
                var profile = await MediaEncodingProfile.CreateFromFileAsync(input).AsTask(ct).ConfigureAwait(false);
                if (await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile).AsTask(ct).ConfigureAwait(false) != TranscodeFailureReason.None) throw new MediaEngineUnsupportedException();
                return;
            }
            MediaEncodingProfile encoding;
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
            if (request.Operation == MediaOperation.CompressVideo)
            {
                var info = Details(request); var size = MediaPlanning.CompressionSize(info.Width ?? 0, info.Height ?? 0, Option(request, "Quality", "medium"));
                encoding = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
                encoding.Video.Width = (uint)size.Width; encoding.Video.Height = (uint)size.Height; encoding.Video.Bitrate = (uint)size.Bitrate;
                if (!Flag(request, "KeepAudio", true) || !info.HasAudio) encoding.Audio = null;
                else encoding.Audio.Bitrate = 128000;
            }
            else
            {
                // MediaClip accepts video files. The native transcoder handles audio-only
                // trim ranges while retaining the source container and encoding profile.
                encoding = await MediaEncodingProfile.CreateFromFileAsync(input).AsTask(ct).ConfigureAwait(false);
                if (string.Equals(input.FileType, ".wav", StringComparison.OrdinalIgnoreCase))
                {
                    // The profile read from a WAV file has no container, which the transcoder
                    // rejects; a WAVE profile carrying the source's PCM format works.
                    var wav = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
                    wav.Audio = encoding.Audio;
                    encoding = wav;
                }
                transcoder.TrimStartTime = Time(request, "Start");
                if (Option(request, "End").Length > 0) transcoder.TrimStopTime = Time(request, "End");
            }
            var prepared = await transcoder.PrepareFileTranscodeAsync(input, output, encoding).AsTask(ct).ConfigureAwait(false);
            if (!prepared.CanTranscode)
            {
                // Hardware encoders reject some inputs (for example unusual frame rates) that the
                // software path handles; try that before giving up.
                transcoder.HardwareAccelerationEnabled = false;
                prepared = await transcoder.PrepareFileTranscodeAsync(input, output, encoding).AsTask(ct).ConfigureAwait(false);
            }
            if (!prepared.CanTranscode) throw new MediaEngineUnsupportedException();
            await prepared.TranscodeAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MediaEngineUnsupportedException)
        { throw new MediaEngineUnsupportedException(ex); }
    }
    private static async Task MergeAsync(StorageFile first, string destination, MediaRequest r, CancellationToken ct)
    {
        var paths = new[] { first.Path }.Concat(r.AdditionalInputs ?? []).ToArray();
        if (paths.Length is < 2 or > 100 || r.InputDetails?.Count != paths.Length) throw MediaMessages.ProcessingFailed();
        bool video = r.Target == MediaTargets.Mp4; var composition = new MediaComposition();
        if (video)
        {
            foreach (string path in paths)
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct).ConfigureAwait(false);
                composition.Clips.Add(await MediaClip.CreateFromFileAsync(file).AsTask(ct).ConfigureAwait(false));
            }
        }
        else
        {
            TimeSpan total = TimeSpan.Zero;
            foreach (string path in paths)
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct).ConfigureAwait(false);
                var track = await BackgroundAudioTrack.CreateFromFileAsync(file).AsTask(ct).ConfigureAwait(false);
                track.Delay = total; total += track.OriginalDuration; composition.BackgroundAudioTracks.Add(track);
            }
            // A silent visual duration anchor allows sequential background tracks to render
            // as audio-only; the selected M4A profile has no video stream.
            composition.Clips.Add(MediaClip.CreateFromColor(global::Windows.UI.Color.FromArgb(255, 0, 0, 0), total));
        }
        await using (File.Create(destination)) { }
        var output = await StorageFile.GetFileFromPathAsync(destination).AsTask(ct).ConfigureAwait(false);
        var profile = video ? MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto) : MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
        if (video)
        {
            var info = Details(r); profile.Video.Width = (uint)((info.Width ?? 2) / 2 * 2); profile.Video.Height = (uint)((info.Height ?? 2) / 2 * 2);
            if (!r.InputDetails!.Any(i => i.HasAudio)) profile.Audio = null;
        }
        else profile.Video = null;
        if (await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile).AsTask(ct).ConfigureAwait(false) != TranscodeFailureReason.None) throw new MediaEngineUnsupportedException();
    }
    private async Task ExtractAsync(StorageFile input, string destination, MediaRequest r, CancellationToken ct)
    {
        var info = Details(r); if (!info.HasVideo || (long)info.Width!.Value * info.Height!.Value > 80000000) throw MediaMessages.ProcessingFailed();
        var composition = new MediaComposition(); composition.Clips.Add(await MediaClip.CreateFromFileAsync(input).AsTask(ct).ConfigureAwait(false));
        bool gif = r.Operation == MediaOperation.ToGif; int fps = Int(r, "Fps", 12);
        bool single = Option(r, "Mode", "single") == "single";
        var times = gif ? MediaPlanning.GifTimes(Time(r, "Start"), TimeSpan.FromSeconds(Int(r, "Duration", 5)), fps, info.Duration) :
            MediaPlanning.FrameTimes(info.Duration, single, single ? Time(r, "At") : TimeSpan.Zero, single ? TimeSpan.FromSeconds(1) : Time(r, "EverySeconds", "1"));
        int width = gif ? Int(r, "Width", 480) : info.Width.Value;
        int height = gif ? MediaPlanning.GifSize(info.Width.Value, info.Height.Value, width).Height : info.Height.Value;
        if (gif && (width is < 100 or > 1000 || height is < 1 or > 2000)) throw MediaMessages.ProcessingFailed();
        async IAsyncEnumerable<MediaGifFrame> Frames([EnumeratorCancellation] CancellationToken token)
        {
            for (int i = 0; i < times.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                using var image = await composition.GetThumbnailAsync(times[i], width, height, VideoFramePrecision.NearestFrame).AsTask(token).ConfigureAwait(false);
                var decoder = await BitmapDecoder.CreateAsync(image).AsTask(token).ConfigureAwait(false);
                var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    new BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = (uint)height }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(token).ConfigureAwait(false);
                yield return new(width, height, pixels.DetachPixelData(), gif ? MediaPlanning.GifDelay(i, fps) : 10);
            }
        }
        if (gif)
        {
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await _gifEncoder.EncodeAsync(Frames(ct), output, ct).ConfigureAwait(false); return;
        }
        string format = Option(r, "Format", "jpg"); if (format is not ("jpg" or "png")) throw MediaMessages.ProcessingFailed();
        int index = 0;
        await foreach (var frame in Frames(ct).ConfigureAwait(false))
        {
            string name = MediaPlanning.FrameName(Option(r, "Stem", "frame"), times[index++], format);
            await using var stream = new FileStream(Path.Combine(destination, name), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var random = stream.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(format == "png" ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId, random).AsTask(ct).ConfigureAwait(false);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)frame.Width, (uint)frame.Height, 96, 96, frame.Bgra32);
            await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
        }
    }
}
