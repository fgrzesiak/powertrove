using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FileKit.Text;

public enum SubtitleFormat { Srt, Vtt }
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text, string? Identifier = null, string Settings = "");
public sealed record SubtitleDocument(IReadOnlyList<SubtitleCue> Cues);
public sealed class SubtitleException(string message) : Exception(message);

/// <summary>Bounded SRT/WebVTT cue parsing and source-preserving time arithmetic.
/// WebVTT style/region/note/header metadata is rejected rather than silently discarded.</summary>
public static partial class Subtitles
{
    public const int MaxBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan MaxTime = TimeSpan.FromDays(7);
    public static async Task<SubtitleDocument> ReadAsync(Stream input, SubtitleFormat format, CancellationToken ct = default)
    {
        using var memory = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > MaxBytes) throw new SubtitleException("Subtitle byte limit exceeded.");
            await memory.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        try
        {
            string text = new UTF8Encoding(false, true).GetString(memory.GetBuffer(), 0, (int)memory.Length);
            return await Task.Run(() => Parse(text, format, ct), ct).ConfigureAwait(false);
        }
        catch (DecoderFallbackException) { throw new SubtitleException("Subtitles must be valid UTF-8."); }
    }
    public static SubtitleDocument Parse(string text, SubtitleFormat format, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes || text.Contains('\0')) throw new SubtitleException("Invalid subtitle size or content.");
        string[] lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        int index = 0;
        if (format == SubtitleFormat.Vtt)
        {
            if (lines.Length < 2 || !(lines[0] == "WEBVTT" || lines[0].StartsWith("WEBVTT ", StringComparison.Ordinal) || lines[0].StartsWith("WEBVTT\t", StringComparison.Ordinal)) || lines[0].Contains("-->"))
                throw new SubtitleException("Missing WebVTT header.");
            index = 1;
            if (lines[index].Length != 0) throw new SubtitleException("WebVTT header metadata is not supported.");
        }
        var cues = new List<SubtitleCue>();
        while (index < lines.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[index])) { index++; continue; }
            if (cues.Count >= 100000) throw new SubtitleException("Subtitle cue limit exceeded.");
            string? identifier = null;
            if (!lines[index].Contains("-->"))
            {
                identifier = lines[index++];
                if (format == SubtitleFormat.Srt && (!int.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1))
                    throw new SubtitleException("Invalid SRT cue index.");
                if (format == SubtitleFormat.Vtt && (identifier is "STYLE" or "REGION" or "NOTE" || identifier.StartsWith("NOTE ", StringComparison.Ordinal) || identifier.StartsWith("NOTE\t", StringComparison.Ordinal)))
                    throw new SubtitleException("WebVTT metadata blocks are not supported.");
            }
            if (index >= lines.Length) throw new SubtitleException("Missing cue timing.");
            var match = Timing().Match(lines[index++]);
            if (!match.Success || !Timestamp(match.Groups[1].Value, format, out var start) || !Timestamp(match.Groups[2].Value, format, out var end) || end <= start)
                throw new SubtitleException("Invalid subtitle timing.");
            string settings = match.Groups[3].Value.Trim();
            if (format == SubtitleFormat.Srt && settings.Length > 0) throw new SubtitleException("SRT cue settings are unsupported.");
            if (settings.Length > 0 && !ValidSettings(settings)) throw new SubtitleException("Invalid or unsupported WebVTT cue settings.");
            var body = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index])) { ct.ThrowIfCancellationRequested(); body.Add(lines[index++]); }
            if (body.Count == 0) throw new SubtitleException("Empty subtitle cue.");
            cues.Add(new(start, end, string.Join("\n", body), identifier, settings));
        }
        if (cues.Count == 0) throw new SubtitleException("No subtitle cues.");
        return new(Array.AsReadOnly(cues.ToArray()));
    }
    private static bool Timestamp(string text, SubtitleFormat format, out TimeSpan value)
    {
        value = default;
        var fields = text.Split(':');
        if (fields.Length is < 2 or > 3 || format == SubtitleFormat.Srt && fields.Length != 3) return false;
        char separator = format == SubtitleFormat.Srt ? ',' : '.';
        var seconds = fields[^1].Split(separator);
        if (seconds.Length != 2 || seconds[0].Length != 2 || seconds[1].Length != 3 || fields[^2].Length != 2) return false;
        if (!int.TryParse(seconds[0], NumberStyles.None, CultureInfo.InvariantCulture, out int s) || s >= 60 ||
            !int.TryParse(seconds[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ms) ||
            !int.TryParse(fields[^2], NumberStyles.None, CultureInfo.InvariantCulture, out int m) || m >= 60) return false;
        int hours = 0;
        if (fields.Length == 3 && (fields[0].Length < 2 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours > 168)) return false;
        value = TimeSpan.FromMilliseconds(((hours * 60L + m) * 60 + s) * 1000 + ms);
        return value <= MaxTime;
    }
    public static SubtitleDocument Shift(SubtitleDocument document, TimeSpan offset, CancellationToken ct = default)
    {
        var cues = new List<SubtitleCue>();
        foreach (var cue in document.Cues)
        {
            ct.ThrowIfCancellationRequested();
            if (InlineTimestamp().IsMatch(cue.Text)) throw new SubtitleException("Inline WebVTT timestamps cannot be shifted safely.");
            TimeSpan start, end;
            try { start = cue.Start + offset; end = cue.End + offset; }
            catch (OverflowException) { throw new SubtitleException("Shifted timestamp is outside supported bounds."); }
            if (start < TimeSpan.Zero || end > MaxTime) throw new SubtitleException("Shift would put timestamps before zero or beyond seven days.");
            cues.Add(cue with { Start = start, End = end });
        }
        return new(Array.AsReadOnly(cues.ToArray()));
    }
    public static string Write(SubtitleDocument document, SubtitleFormat format, bool converting = false, CancellationToken ct = default)
    {
        var output = new StringBuilder(format == SubtitleFormat.Vtt ? "WEBVTT\r\n\r\n" : ""); int number = 0;
        foreach (var cue in document.Cues)
        {
            ct.ThrowIfCancellationRequested();
            if (converting && (cue.Settings.Length > 0 || Tags().Matches(cue.Text).Any(m => !AllowedTag().IsMatch(m.Value))))
                throw new SubtitleException("Cue settings or markup cannot be represented in the target format.");
            if (cue.Start < TimeSpan.Zero || cue.End <= cue.Start || cue.End > MaxTime || string.IsNullOrEmpty(cue.Text)) throw new SubtitleException("Invalid cue.");
            if (format == SubtitleFormat.Srt) output.Append(++number).Append("\r\n");
            else if (!string.IsNullOrEmpty(cue.Identifier)) output.Append(cue.Identifier).Append("\r\n");
            output.Append(Stamp(cue.Start, format)).Append(" --> ").Append(Stamp(cue.End, format));
            if (format == SubtitleFormat.Vtt && cue.Settings.Length > 0) output.Append(' ').Append(cue.Settings);
            output.Append("\r\n").Append(cue.Text.Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n\r\n");
            if (output.Length > MaxBytes) throw new SubtitleException("Subtitle output limit exceeded.");
        }
        string result = output.ToString();
        if (Encoding.UTF8.GetByteCount(result) > MaxBytes) throw new SubtitleException("Subtitle output byte limit exceeded.");
        return result;
    }
    private static bool ValidSettings(string settings)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        bool Percent(string text) => text.EndsWith('%') && decimal.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number) && number is >= 0 and <= 100;
        foreach (string token in settings.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split(':'); if (parts.Length != 2 || !names.Add(parts[0])) return false;
            var value = parts[1].Split(',');
            bool valid = parts[0] switch
            {
                "vertical" => value.Length == 1 && value[0] is "rl" or "lr",
                "align" => value.Length == 1 && value[0] is "start" or "center" or "end" or "left" or "right",
                "size" => value.Length == 1 && Percent(value[0]),
                "position" => value.Length <= 2 && (value[0] == "auto" || Percent(value[0])) && (value.Length == 1 || value[1] is "line-left" or "center" or "line-right" or "auto"),
                "line" => value.Length <= 2 && (value[0] == "auto" || Percent(value[0]) || int.TryParse(value[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) && (value.Length == 1 || value[1] is "start" or "center" or "end"),
                _ => false
            };
            if (!valid) return false;
        }
        return true;
    }
    private static string Stamp(TimeSpan time, SubtitleFormat format) => string.Create(CultureInfo.InvariantCulture,
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}{(format == SubtitleFormat.Srt ? ',' : '.')}{time.Milliseconds:000}");
    [GeneratedRegex(@"^([^\s]+)[ \t]+-->[ \t]+([^\s]+)(?:[ \t]+(.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Timing();
    [GeneratedRegex(@"<\d{2,}:")]
    private static partial Regex InlineTimestamp();
    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();
    [GeneratedRegex(@"^</?[biu]>$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllowedTag();
}
