using PowerTrove.Pack.Media;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace PowerTrove.Pack.Media.Windows;

public sealed class WindowsMediaInspector : IMediaInspector
{
    public bool IsAvailable => OperatingSystem.IsWindows();
    public async Task<MediaDetails> InspectAsync(string path, CancellationToken ct)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(ct).ConfigureAwait(false);
            var video = await file.Properties.GetVideoPropertiesAsync().AsTask(ct).ConfigureAwait(false);
            var music = await file.Properties.GetMusicPropertiesAsync().AsTask(ct).ConfigureAwait(false);
            var properties = await file.Properties.RetrievePropertiesAsync(new[] { "System.Video.FrameRate", "System.Audio.ChannelCount", "System.Audio.SampleRate" }).AsTask(ct).ConfigureAwait(false);
            double? Number(string key)
            {
                if (!properties.TryGetValue(key, out var value) || value is null) return null;
                try { double n = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); return double.IsFinite(n) && n > 0 ? n : null; }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
            }
            var duration = video.Duration > TimeSpan.Zero ? video.Duration : music.Duration;
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(7) || (long)video.Width * video.Height > 80000000 || video.Width > 32768 || video.Height > 32768) throw MediaMessages.InspectFailed();
            MediaEncodingProfile? profile = null;
            try { profile = await MediaEncodingProfile.CreateFromFileAsync(file).AsTask(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            int? width = video.Width > 0 ? checked((int)video.Width) : null, height = video.Height > 0 ? checked((int)video.Height) : null;
            if (video.Orientation is global::Windows.Storage.FileProperties.VideoOrientation.Rotate90 or global::Windows.Storage.FileProperties.VideoOrientation.Rotate270) (width, height) = (height, width);
            double? fps = Number("System.Video.FrameRate") / 1000;
            double? channel = Number("System.Audio.ChannelCount");
            int? channels = channel is > 0 and <= 64 ? (int)channel.Value : null;
            double? rate = Number("System.Audio.SampleRate");
            int? sampleRate = rate is > 0 and <= 768000 ? (int)rate.Value : null;
            long? bitrate = video.Bitrate > 0 ? video.Bitrate : music.Bitrate > 0 ? music.Bitrate : null;
            if (width.HasValue && profile is null && !channels.HasValue) throw MediaMessages.InspectFailed();
            return new(duration, width, height, fps is > 0 and < 10000 ? fps : null,
                MediaTechnical.Codec(profile?.Video?.Subtype), MediaTechnical.Codec(profile?.Audio?.Subtype), bitrate, channels, sampleRate, profile?.Audio is not null || channels.HasValue || !width.HasValue && music.Duration > TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PowerTrove.Abstractions.Files.FileProcessingException)
        { throw MediaMessages.InspectFailed(ex); }
    }
}
