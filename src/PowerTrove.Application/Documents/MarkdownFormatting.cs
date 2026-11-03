namespace PowerTrove.Application.Documents;

/// <summary>One replacement in the editor text plus the selection to show afterwards.</summary>
public sealed record TextEdit(int Start, int Length, string Replacement, int SelectionStart, int SelectionLength);

public enum MarkdownLineStyle { Paragraph, Heading1, Heading2, Heading3, Bullet, Numbered, Task, Quote }

/// <summary>
/// Formatting commands of the Markdown editor's toolbar. Each returns a single <see cref="TextEdit"/>, so the
/// text box applies it as one undoable change. Works with any line breaks ("\r" as a WinUI TextBox reports
/// them, "\n" or "\r\n"); inserted line breaks use <paramref name="newLine"/>.
/// </summary>
public static class MarkdownFormatting
{
    /// <summary>Bold, italic, strikethrough or inline code: wraps the selection, or unwraps it when already wrapped.</summary>
    public static TextEdit ToggleWrap(string text, int start, int length, string marker, string placeholder)
    {
        (start, length) = Clamp(text, start, length);
        // Selection includes the markers: remove them.
        if (length >= 2 * marker.Length && text.AsSpan(start, length).StartsWith(marker) && text.AsSpan(start, length).EndsWith(marker))
        {
            string inner = text.Substring(start + marker.Length, length - 2 * marker.Length);
            return new(start, length, inner, start, inner.Length);
        }
        // Markers directly around the selection: remove them.
        if (start >= marker.Length && start + length + marker.Length <= text.Length
            && text.AsSpan(start - marker.Length, marker.Length).SequenceEqual(marker) && text.AsSpan(start + length, marker.Length).SequenceEqual(marker)
            && (marker != "*" || !IsDouble(text, start, length)))
            return new(start - marker.Length, length + 2 * marker.Length, text.Substring(start, length), start - marker.Length, length);
        // Markdown emphasis must not start or end with spaces: keep selected spaces outside the markers.
        string selected = text.Substring(start, length);
        string trimmed = selected.Trim();
        if (trimmed.Length == 0)
            return new(start, length, selected + marker + placeholder + marker, start + selected.Length + marker.Length, placeholder.Length);
        int lead = selected.Length - selected.TrimStart().Length;
        string replacement = selected[..lead] + marker + trimmed + marker + selected[(lead + trimmed.Length)..];
        return new(start, length, replacement, start + lead + marker.Length, trimmed.Length);
    }

    /// <summary>Applies a line style to every line the selection touches; applying the present style again removes it.</summary>
    public static TextEdit SetLineStyle(string text, int start, int length, MarkdownLineStyle style)
    {
        (start, length) = Clamp(text, start, length);
        int blockStart = LineStart(text, start);
        int blockEnd = LineEnd(text, length > 0 && IsBreak(text[start + length - 1]) ? start + length - 1 : start + length);
        var (lines, breaks) = Split(text.Substring(blockStart, blockEnd - blockStart));
        bool remove = style != MarkdownLineStyle.Paragraph && lines.Where(l => l.Trim().Length > 0).DefaultIfEmpty(string.Empty).All(l => StyleOf(l) == style);
        int number = 1;
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            string content = StripPrefix(lines[i], out _);
            bool blank = content.Trim().Length == 0 && lines.Count > 1;
            string prefix = remove || blank ? string.Empty : style switch
            {
                MarkdownLineStyle.Heading1 => "# ",
                MarkdownLineStyle.Heading2 => "## ",
                MarkdownLineStyle.Heading3 => "### ",
                MarkdownLineStyle.Bullet => "- ",
                MarkdownLineStyle.Numbered => (number++).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". ",
                MarkdownLineStyle.Task => "- [ ] ",
                MarkdownLineStyle.Quote => "> ",
                _ => string.Empty
            };
            result.Append(prefix).Append(content);
            if (i < breaks.Count) result.Append(breaks[i]);
        }
        string replacement = result.ToString();
        // A collapsed caret stays at the end of its line's text; a selection covers the restyled lines.
        int caret = length == 0 ? blockStart + replacement.Length : blockStart;
        return new(blockStart, blockEnd - blockStart, replacement, caret, length == 0 ? 0 : replacement.Length);
    }

    /// <summary>A link around the selected text, with the address selected for typing.</summary>
    public static TextEdit InsertLink(string text, int start, int length, string textPlaceholder, string address = "https://")
    {
        (start, length) = Clamp(text, start, length);
        string label = text.Substring(start, length).Trim();
        if (label.Length == 0)
            return new(start, length, "[" + textPlaceholder + "](" + address + ")", start + 1, textPlaceholder.Length);
        string replacement = "[" + label + "](" + address + ")";
        return new(start, length, replacement, start + label.Length + 3, address.Length);
    }

    /// <summary>An image reference; the description is selected for typing.</summary>
    public static TextEdit InsertImage(string text, int start, int length, string description, string path)
    {
        (start, length) = Clamp(text, start, length);
        string alt = text.Substring(start, length).Trim() is { Length: > 0 } selected ? selected : description;
        string destination = path.Contains(' ') || path.Contains('(') || path.Contains(')') ? "<" + path + ">" : path;
        string replacement = "![" + alt + "](" + destination + ")";
        return new(start, length, replacement, start + 2, alt.Length);
    }

    /// <summary>Inline code for part of a line, a fenced block for whole or several lines.</summary>
    public static TextEdit ToggleCode(string text, int start, int length, string newLine, string placeholder)
    {
        (start, length) = Clamp(text, start, length);
        string selected = text.Substring(start, length);
        bool block = selected.IndexOfAny(['\r', '\n']) >= 0 || length > 0 && start == LineStart(text, start) && start + length == LineEnd(text, start + length);
        if (!block) return ToggleWrap(text, start, length, "`", placeholder);
        string body = selected.Length == 0 ? placeholder : selected.TrimEnd('\r', '\n');
        return InsertBlock(text, start, length, "```" + newLine + body + newLine + "```", newLine, 3 + newLine.Length, body.Length);
    }

    /// <summary>Inserts a block (table, rule, code) on its own lines, separated by blank lines.</summary>
    public static TextEdit InsertBlock(string text, int start, int length, string block, string newLine, int selectFrom = 0, int selectLength = 0)
    {
        (start, length) = Clamp(text, start, length);
        string before = text[..start], after = text[(start + length)..];
        string lead = before.Length == 0 ? string.Empty : EndsWithBlankLine(before) ? string.Empty : EndsWithBreak(before) ? newLine : newLine + newLine;
        string trail = after.Length == 0 ? newLine : StartsWithBlankLine(after) ? string.Empty : StartsWithBreak(after) ? newLine : newLine + newLine;
        string replacement = lead + block + trail;
        return new(start, length, replacement, start + lead.Length + selectFrom, selectLength);
    }

    /// <summary>A two-column table template with the first header cell selected.</summary>
    public static TextEdit InsertTable(string text, int start, int length, string newLine, string column, string cell)
    {
        string header = "| " + column + " 1 | " + column + " 2 |";
        string table = header + newLine + "| --- | --- |" + newLine + "| " + cell + " | " + cell + " |";
        return InsertBlock(text, start, length, table, newLine, 2, column.Length + 2);
    }

    public static MarkdownLineStyle StyleOf(string line)
    {
        StripPrefix(line, out var style);
        return style;
    }

    private static string StripPrefix(string line, out MarkdownLineStyle style)
    {
        style = MarkdownLineStyle.Paragraph;
        var match = System.Text.RegularExpressions.Regex.Match(line, @"^(#{1,6} |- \[[ xX]\] |[-*+] |\d{1,9}[.)] |> ?)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!match.Success) return line;
        string prefix = match.Value;
        style = prefix switch
        {
            "# " => MarkdownLineStyle.Heading1,
            "## " => MarkdownLineStyle.Heading2,
            "### " => MarkdownLineStyle.Heading3,
            _ when prefix.StartsWith('#') => MarkdownLineStyle.Heading3,
            _ when prefix.StartsWith("- [", StringComparison.Ordinal) => MarkdownLineStyle.Task,
            _ when prefix[0] is '-' or '*' or '+' => MarkdownLineStyle.Bullet,
            _ when char.IsAsciiDigit(prefix[0]) => MarkdownLineStyle.Numbered,
            _ => MarkdownLineStyle.Quote
        };
        return line[prefix.Length..];
    }

    private static bool IsDouble(string text, int start, int length) =>
        start >= 2 && text[start - 2] == '*' && start + length + 1 < text.Length && text[start + length + 1] == '*';

    private static (int Start, int Length) Clamp(string text, int start, int length)
    {
        start = Math.Clamp(start, 0, text.Length);
        return (start, Math.Clamp(length, 0, text.Length - start));
    }

    private static bool IsBreak(char c) => c is '\r' or '\n';
    private static int LineStart(string text, int index) { while (index > 0 && !IsBreak(text[index - 1])) index--; return index; }
    private static int LineEnd(string text, int index) { while (index < text.Length && !IsBreak(text[index])) index++; return index; }
    private static bool EndsWithBreak(string text) => text.Length > 0 && IsBreak(text[^1]);
    private static bool StartsWithBreak(string text) => text.Length > 0 && IsBreak(text[0]);
    private static bool EndsWithBlankLine(string text) => Split(text).Lines is var lines && lines.Count >= 2 && lines[^1].Length == 0 && lines[^2].Trim().Length == 0;
    private static bool StartsWithBlankLine(string text) => Split(text).Lines is var lines && lines.Count >= 2 && lines[0].Length == 0 && lines[1].Trim().Length == 0;

    /// <summary>Lines and the exact separators between them ("\r\n", "\r" or "\n").</summary>
    private static (List<string> Lines, List<string> Breaks) Split(string text)
    {
        var lines = new List<string>(); var breaks = new List<string>();
        int from = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (!IsBreak(text[i])) continue;
            lines.Add(text[from..i]);
            string separator = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : text[i].ToString();
            breaks.Add(separator); i += separator.Length - 1; from = i + 1;
        }
        lines.Add(text[from..]);
        return (lines, breaks);
    }
}
