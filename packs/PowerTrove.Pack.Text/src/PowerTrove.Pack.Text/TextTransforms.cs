using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerTrove.Pack.Text;

/// <summary>Pure transforms. No IO, no implicit file access and no shared mutable state.</summary>
public static class TextTransforms
{
    public const int MaximumCharacters = 65536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    public static string Apply(string mode, string text, string pattern = "", string replacement = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumCharacters || pattern.Length > 1024 || replacement.Length > 4096)
            throw new ArgumentException(SR.Get("TooLarge"));
        string value = mode switch
        {
            "trim" => text.Trim(),
            "trim-lines" => Lines(text).Select(line => line.Trim()).JoinLines(),
            "collapse-whitespace" => Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant, RegexTimeout).Trim(),
            "remove-empty-lines" => Lines(text).Where(line => !string.IsNullOrWhiteSpace(line)).JoinLines(),
            "unique-lines" => Lines(text).Distinct(StringComparer.Ordinal).JoinLines(),
            "sort-lines" => Lines(text).OrderBy(line => line, StringComparer.OrdinalIgnoreCase).ThenBy(line => line, StringComparer.Ordinal).JoinLines(),
            "lowercase" => text.ToLower(CultureInfo.CurrentCulture),
            "uppercase" => text.ToUpper(CultureInfo.CurrentCulture),
            "titlecase" => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower(CultureInfo.CurrentCulture)),
            "regex-replace" => ReplaceRegex(text, pattern, replacement),
            "url-encode" => Uri.EscapeDataString(text),
            "url-decode" => WebUtility.UrlDecode(text),
            "html-encode" => WebUtility.HtmlEncode(text),
            "html-decode" => WebUtility.HtmlDecode(text),
            "base64-encode" => Convert.ToBase64String(StrictUtf8.GetBytes(text)),
            "base64-decode" => StrictUtf8.GetString(Convert.FromBase64String(text)),
            "json-escape" => JsonSerializer.Serialize(text),
            _ => throw new ArgumentException(SR.Get("InvalidOperation"))
        };
        if (value.Length > MaximumCharacters)
            throw new ArgumentException(SR.Get("TooLarge"));
        return value;
    }

    private static string ReplaceRegex(string text, string pattern, string replacement)
    {
        if (string.IsNullOrEmpty(pattern))
            throw new ArgumentException(SR.Get("PatternRequired"));
        // Bound expansion while matching, so a pattern matching every character cannot create
        // hundreds of megabytes before the final output-size check.
        var regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
        int consumed = 0, produced = 0;
        return regex.Replace(text, match =>
        {
            string value = match.Result(replacement); // Preserve .NET group replacement semantics.
            produced = checked(produced + match.Index - consumed + value.Length);
            if (produced > MaximumCharacters) throw new ArgumentException(SR.Get("TooLarge"));
            consumed = match.Index + match.Length;
            return value;
        });
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string JoinLines(this IEnumerable<string> lines) => string.Join("\n", lines);
}
