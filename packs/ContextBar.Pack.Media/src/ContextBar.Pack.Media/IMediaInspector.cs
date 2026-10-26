using System.Globalization;

namespace ContextBar.Pack.Media;

/// <summary>Technical facts only. No title, artist, comments, tags or raw tool output.</summary>
public sealed record MediaDetails(TimeSpan Duration, int? Width = null, int? Height = null, double? FrameRate = null,
    string? VideoCodec = null, string? AudioCodec = null, long? Bitrate = null, int? Channels = null,
    int? SampleRate = null, bool HasAudio = false, string? CopySignature = null)
{
    public bool HasVideo => Width > 0 && Height > 0;
}
public interface IMediaInspector
{
    bool IsAvailable { get; }
    Task<MediaDetails> InspectAsync(string path, CancellationToken cancellationToken);
}
public sealed class CompositeMediaInspector(IEnumerable<IMediaInspector> inspectors) : IMediaInspector
{
    private readonly IReadOnlyList<IMediaInspector> _inspectors = inspectors.ToArray();
    public bool IsAvailable => _inspectors.Any(i => i.IsAvailable);
    public async Task<MediaDetails> InspectAsync(string path, CancellationToken ct)
    {
        foreach (var inspector in _inspectors.Where(i => i.IsAvailable))
        {
            ct.ThrowIfCancellationRequested();
            try { return await inspector.InspectAsync(path, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is FileProcessingException or IOException or NotSupportedException) { }
        }
        throw MediaMessages.InspectFailed();
    }
}
public static class MediaTechnical
{
    public static string? Codec(string? value) => value is { Length: > 0 and <= 48 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? value : null;
    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    public static string Format(MediaDetails details)
    {
        string unknown = SR.GetString("MediaUnknown");
        return string.Format(CultureInfo.CurrentCulture, SR.GetString("MediaDetailsFormat"),
            ((long)details.Duration.TotalHours).ToString("00", CultureInfo.InvariantCulture) + details.Duration.ToString(@"\:mm\:ss\.fff", CultureInfo.InvariantCulture),
            details.HasVideo ? $"{details.Width} × {details.Height}" : unknown,
            details.FrameRate is > 0 and < 10000 ? details.FrameRate.Value.ToString("0.###", CultureInfo.CurrentCulture) : unknown,
            Codec(details.VideoCodec) ?? unknown, Codec(details.AudioCodec) ?? unknown,
            details.Bitrate is > 0 ? (details.Bitrate.Value / 1000d).ToString("0.###", CultureInfo.CurrentCulture) : unknown,
            details.Channels is > 0 and <= 64 ? details.Channels.Value.ToString(CultureInfo.CurrentCulture) : unknown);
    }
}
public sealed record MediaGifFrame(int Width, int Height, byte[] Bgra32, int DelayCentiseconds);
public interface IGifFrameEncoder
{
    Task EncodeAsync(IAsyncEnumerable<MediaGifFrame> frames, Stream destination, CancellationToken cancellationToken);
}
public sealed class MediaEngineUnsupportedException : Exception
{
    public MediaEngineUnsupportedException(Exception? inner = null) : base("The installed media engine does not support this input.", inner) { }
}
