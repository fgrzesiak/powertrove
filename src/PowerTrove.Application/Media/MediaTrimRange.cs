using FileKit.Text;

namespace PowerTrove.Application.Media;

/// <summary>Pure timeline state. No decoder, action implementation, foreground capture or file writes.</summary>
public sealed class MediaTrimRange
{
    public static readonly TimeSpan MinimumLength = TimeSpan.FromMilliseconds(1);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".aac", ".ogg", ".flac", ".m4a", ".wma", ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm" };
    public TimeSpan Duration { get; }
    public TimeSpan Start { get; private set; }
    public TimeSpan End { get; private set; }
    public TimeSpan Position { get; private set; }
    public TimeSpan Length => End - Start;
    public bool Precise { get; set; }
    public bool InitialRangeAdjusted { get; private set; }

    public MediaTrimRange(TimeSpan duration, IReadOnlyDictionary<string, string> parameters)
    {
        if (duration < MinimumLength || duration > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(duration));
        Duration = duration; End = duration;
        bool valid = MediaTimeText.TryParse(parameters.GetValueOrDefault("Start", "0"), out var start);
        string endText = parameters.GetValueOrDefault("End", "");
        TimeSpan end = duration;
        valid &= string.IsNullOrWhiteSpace(endText) || MediaTimeText.TryParse(endText, out end);
        if (valid && start >= TimeSpan.Zero && end <= duration && end - start >= MinimumLength) { Start = start; End = end; }
        else InitialRangeAdjusted = true;
        Position = Start;
        Precise = parameters.TryGetValue("Precise", out var precise) && bool.TryParse(precise, out bool value) && value;
    }
    public static bool CanEdit(ContextSnapshot? snapshot) => snapshot is { Status: ContextStatus.Available, IsStale: false, Content: FileSelection { Files.Count: 1 } selection }
        && selection.Files[0] is { IsDirectory: false, IsAccessible: true, IsAvailableLocally: true, Path: not null } file
        && Extensions.Contains(file.Extension);
    public void SetStart(double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        Start = TimeSpan.FromMilliseconds(Math.Clamp(Math.Round(seconds * 1000), 0, (End - MinimumLength).TotalMilliseconds));
        InitialRangeAdjusted = false;
    }
    public void SetEnd(double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        End = seconds >= Duration.TotalSeconds ? Duration :
            TimeSpan.FromMilliseconds(Math.Clamp(Math.Round(seconds * 1000), (Start + MinimumLength).TotalMilliseconds, Duration.TotalMilliseconds));
        InitialRangeAdjusted = false;
    }
    public void SetPosition(TimeSpan position) => Position = position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position;
    public TimeSpan PreparePlayback()
    {
        if (Position < Start || Position >= End) Position = Start;
        return Position;
    }
    public bool ReachedEnd(TimeSpan position) => position >= End;
    public DictionaryParameters Parameters() => new(new Dictionary<string, string>
    {
        // Let the engine resolve the full source end: container/player metadata can differ by a few audio-padding samples.
        ["Start"] = MediaTimeText.Seconds(Start), ["End"] = End == Duration ? string.Empty : MediaTimeText.Seconds(End), ["Precise"] = Precise ? "true" : "false"
    });
}
