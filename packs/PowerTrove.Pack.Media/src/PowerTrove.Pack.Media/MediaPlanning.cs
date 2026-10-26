using System.Globalization;

namespace PowerTrove.Pack.Media;

public static class MediaTime
{
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return false;
        var fields = text.Trim().Split(':');
        if (fields.Length is < 1 or > 3 || fields.Any(string.IsNullOrEmpty)) return false;
        if (!decimal.TryParse(fields[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal seconds) || seconds < 0 || fields.Length > 1 && seconds >= 60) return false;
        decimal total = seconds;
        for (int i = fields.Length - 2, multiplier = 60; i >= 0; i--, multiplier *= 60)
        {
            if (!int.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 0 || (i > 0 && n >= 60)) return false;
            total += (decimal)n * multiplier;
        }
        if (total > 7 * 24 * 3600) return false;
        value = TimeSpan.FromTicks(decimal.ToInt64(decimal.Round(total * TimeSpan.TicksPerSecond, 0, MidpointRounding.AwayFromZero)));
        return true;
    }
    public static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.#######", CultureInfo.InvariantCulture);
    public static string Stamp(TimeSpan time) => ((long)time.TotalMinutes).ToString("00", CultureInfo.InvariantCulture) + "m" + time.Seconds.ToString("00", CultureInfo.InvariantCulture) + "s" +
        (time.Milliseconds == 0 ? "" : time.Milliseconds.ToString("000", CultureInfo.InvariantCulture) + "ms");
}

public sealed record VideoSize(int Width, int Height, int Bitrate);
public static class MediaPlanning
{
    public static VideoSize CompressionSize(int width, int height, string quality)
    {
        if (width is < 2 or > 32768 || height is < 2 or > 32768 || (long)width * height > 80000000) throw new ArgumentOutOfRangeException(nameof(width));
        var (maxWidth, maxHeight, rate) = quality switch { "high" => (1920, 1080, 5000000), "small" => (854, 480, 1000000), _ => (1280, 720, 2500000) };
        // Portrait videos get the same pixel budget with the bounding box transposed.
        if (height > width) (maxWidth, maxHeight) = (maxHeight, maxWidth);
        double scale = Math.Min(1, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        return new(Math.Max(2, (int)(width * scale) / 2 * 2), Math.Max(2, (int)(height * scale) / 2 * 2), rate);
    }
    public static VideoSize GifSize(int sourceWidth, int sourceHeight, int width)
    {
        if (sourceWidth < 1 || sourceHeight < 1 || width is < 100 or > 1000) throw new ArgumentOutOfRangeException(nameof(width));
        int height = checked((int)Math.Max(1, Math.Round(sourceHeight * (double)width / sourceWidth)));
        if (height > 2000) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        return new(width, height, 0);
    }
    public static IReadOnlyList<TimeSpan> GifTimes(TimeSpan start, TimeSpan requested, int fps, TimeSpan mediaDuration)
    {
        if (start < TimeSpan.Zero || start >= mediaDuration || requested <= TimeSpan.Zero || fps is < 5 or > 30) throw new ArgumentOutOfRangeException(nameof(start));
        TimeSpan duration = requested < mediaDuration - start ? requested : mediaDuration - start;
        int count = checked((int)Math.Ceiling(duration.TotalSeconds * fps));
        if (count is < 1 or > 600) throw new ArgumentOutOfRangeException(nameof(requested));
        return Enumerable.Range(0, count).Select(i => start + TimeSpan.FromSeconds((double)i / fps)).ToArray();
    }
    public static IReadOnlyList<TimeSpan> FrameTimes(TimeSpan duration, bool single, TimeSpan at, TimeSpan interval)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        if (single) return at >= TimeSpan.Zero && at < duration ? [at] : throw new ArgumentOutOfRangeException(nameof(at));
        if (interval < TimeSpan.FromSeconds(.5) || interval > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(interval));
        int count = (int)Math.Min(500, Math.Ceiling(duration.TotalSeconds / interval.TotalSeconds));
        return Enumerable.Range(0, count).Select(i => TimeSpan.FromTicks(interval.Ticks * i)).ToArray();
    }
    public static string FrameName(string stem, TimeSpan timestamp, string format) => OutputNames.Sanitize(stem.Length > 180 ? stem[..180] : stem) + " - " + MediaTime.Stamp(timestamp) + "." + format;
    public static int GifDelay(int index, int fps) => (int)Math.Round((index + 1) * 100d / fps, MidpointRounding.AwayFromZero) - (int)Math.Round(index * 100d / fps, MidpointRounding.AwayFromZero);
}
