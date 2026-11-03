using System.Text;
using System.Text.RegularExpressions;

namespace PowerTrove.Pack.Markdown.Conversion;

/// <summary>An image found in a source document; written next to the Markdown file only if the user keeps images.</summary>
internal sealed record ExtractedImage(byte[] Bytes, string Extension);

/// <summary>
/// The result of converting one document. Images are referenced by <see cref="ImageToken"/> placeholders until the
/// action knows where it saved them. Counts only, never content, describe what was left out.
/// </summary>
internal sealed partial record ConvertedMarkdown(string Markdown, IReadOnlyList<ExtractedImage> Images, int OmittedImages = 0, int OmittedContent = 0)
{
    public static string ImageToken(int index) => "\u0001img" + index + "\u0001";

    /// <summary>Replaces the placeholders with saved paths; an image without a path becomes its description.</summary>
    public string Resolve(Func<int, string?> path) => Token().Replace(Markdown, match =>
    {
        string alt = match.Groups[1].Value;
        string? target = path(int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        return target is null ? (alt.Length == 0 ? string.Empty : "*" + alt + "*") : "![" + alt + "](" + target + ")";
    });

    [GeneratedRegex("!\\[([^\\]\\n]*)\\]\\(\u0001img(\\d+)\u0001\\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Token();
}

/// <summary>Builds Markdown text: escapes literal characters and keeps block spacing tidy.</summary>
internal sealed class MarkdownText
{
    /// <summary>Characters of Markdown produced from one document.</summary>
    public const int MaximumLength = 4 * 1024 * 1024;
    public const int MaximumImages = 500;
    public const long MaximumImageBytes = 200L * 1024 * 1024;

    private readonly StringBuilder _text = new();
    private readonly List<ExtractedImage> _images = [];
    private long _imageBytes;
    public int OmittedImages { get; set; }
    public int OmittedContent { get; set; }

    /// <summary>Adds a block (paragraph, heading, list, table) separated by a blank line.</summary>
    public void Block(string block)
    {
        block = block.TrimEnd();
        if (block.Trim().Length == 0) return;
        if (_text.Length > 0) _text.Append("\n\n");
        _text.Append(block);
        if (_text.Length > MaximumLength) throw new FileProcessingException(SR.Get("ToMarkdownTooLarge"));
    }

    /// <summary>Adds a line directly below the previous one (list items).</summary>
    public void Line(string line)
    {
        if (_text.Length > 0) _text.Append('\n');
        _text.Append(line.TrimEnd());
        if (_text.Length > MaximumLength) throw new FileProcessingException(SR.Get("ToMarkdownTooLarge"));
    }

    public bool IsEmpty => _text.Length == 0;

    /// <summary>Whether the text, trimmed, ends with one of the characters.</summary>
    public static bool EndsWithAny(string text, string characters)
    {
        string trimmed = text.TrimEnd();
        return trimmed.Length > 0 && characters.Contains(trimmed[^1], StringComparison.Ordinal);
    }

    /// <summary>The Markdown reference for an image, or its description when the limits are reached.</summary>
    public string Image(byte[] bytes, string extension, string? alt)
    {
        string description = Inline(Clean(alt ?? string.Empty));
        if (_images.Count >= MaximumImages || _imageBytes + bytes.Length > MaximumImageBytes) { OmittedImages++; return description.Length == 0 ? string.Empty : "*" + description + "*"; }
        _imageBytes += bytes.Length;
        _images.Add(new(bytes, extension));
        return "![" + description.Replace("]", "\\]", StringComparison.Ordinal) + "](" + ConvertedMarkdown.ImageToken(_images.Count - 1) + ")";
    }

    public ConvertedMarkdown Build() => new(_text.ToString().Trim('\n') + "\n", _images, OmittedImages, OmittedContent);

    /// <summary>Escapes text so it is read literally inside a paragraph.</summary>
    public static string Inline(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c is '\\' or '*' or '_' or '`' or '[' or ']' or '<' or '>' or '~' or '|') builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>Escapes what would turn the start of a line into a heading, list, quote or rule.</summary>
    public static string LineStart(string line)
    {
        if (line.Length == 0) return line;
        if (line[0] is '#' or '+' or '=' || line[0] == '-' && (line.Length == 1 || line[1] is ' ' or '-'))
            return "\\" + line;
        int digits = 0;
        while (digits < line.Length && digits < 9 && char.IsAsciiDigit(line[digits])) digits++;
        if (digits > 0 && digits < line.Length && line[digits] is '.' or ')' && (digits + 1 == line.Length || line[digits + 1] == ' '))
            return line[..digits] + "\\" + line[digits..];
        return line;
    }

    /// <summary>Collapses whitespace and removes control characters.</summary>
    public static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || c == ' ') { space = true; continue; }
            if (char.IsControl(c) || c is '￾' or '￿') continue;
            if (space && builder.Length > 0) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>A pipe table; the first row is the header. Cells are already Markdown inline text.</summary>
    public static string Table(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        int columns = rows.Max(r => r.Count);
        if (columns == 0) return string.Empty;
        var builder = new StringBuilder();
        for (int r = 0; r < rows.Count; r++)
        {
            builder.Append('|');
            for (int c = 0; c < columns; c++)
                builder.Append(' ').Append(c < rows[r].Count ? Cell(rows[r][c]) : string.Empty).Append(" |");
            builder.Append('\n');
            if (r == 0) builder.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columns))).Append('\n');
        }
        return builder.ToString();
    }

    // Cells arrive escaped by Inline (pipes included); line breaks become the table's only allowed break.
    private static string Cell(string text) => text.Trim().Replace("\r\n", "<br>", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);

    /// <summary>Wraps inline text with a marker, keeping surrounding spaces outside so the emphasis still parses.</summary>
    public static string Wrap(string text, string marker)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return text;
        int lead = text.Length - text.TrimStart().Length, trail = text.Length - text.TrimEnd().Length;
        return text[..lead] + marker + trimmed + marker + text[(text.Length - trail)..];
    }

    /// <summary>Inline code that survives backticks inside the text.</summary>
    public static string Code(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return text;
        int longest = 0, run = 0;
        foreach (char c in trimmed) { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
        string fence = new('`', longest + 1);
        string pad = trimmed.StartsWith('`') || trimmed.EndsWith('`') ? " " : string.Empty;
        return fence + pad + trimmed + pad + fence;
    }

    /// <summary>A fenced code block whose fence is longer than any backtick run inside.</summary>
    public static string Fence(string code, string? language = null)
    {
        int longest = 0, run = 0;
        foreach (char c in code) { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
        string fence = new('`', Math.Max(3, longest + 1));
        return fence + (language ?? string.Empty) + "\n" + code.TrimEnd('\n', '\r') + "\n" + fence;
    }
}

/// <summary>Raised when a document cannot be read; the message is user-facing and names no content.</summary>
internal sealed class DocumentFormatException(string message) : Exception(message);
