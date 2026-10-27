using System.Globalization;

namespace FileKit.Text;

/// <summary>Bounded media time text shared by actions and visual range editors.</summary>
public static class MediaTimeText
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
}
