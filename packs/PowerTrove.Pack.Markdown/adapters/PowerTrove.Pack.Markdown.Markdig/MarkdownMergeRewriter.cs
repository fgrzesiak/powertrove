using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PowerTrove.Pack.Markdown.Markdig;

/// <summary>
/// Rewrites only source spans Markdig located precisely; text it cannot match exactly stays as written,
/// so a merge never alters content it does not understand.
/// </summary>
internal static class MarkdownMergeRewriter
{
    // The same syntax as rendering, so the parts are read exactly as the viewer shows them.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UseYamlFrontMatter()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseFootnotes()
        .UseDefinitionLists()
        .Build();

    public static string Rewrite(string markdown, MarkdownMergePart part, CancellationToken cancellationToken)
    {
        string text = markdown.Replace("\0", "�", StringComparison.Ordinal);
        var document = global::Markdig.Markdown.Parse(text, Pipeline);
        var edits = new List<Edit>();
        var labels = new List<Edit>();
        bool labelsComplete = true;
        string? closeFence = null;
        foreach (var node in document.Descendants())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case YamlFrontMatterBlock front when !part.KeepFrontMatter:
                    int end = Math.Min(text.Length, front.Span.End + 1);
                    while (end < text.Length && text[end] is '\r' or '\n') end++;
                    edits.Add(new(front.Span.Start, end - front.Span.Start, string.Empty));
                    break;
                case LinkInline { IsAutoLink: false } link when link.Url is not null:
                    Destination(text, link.UrlSpan, link.Url, part, edits);
                    break;
                case LinkReferenceDefinition definition when definition is not FootnoteLinkReferenceDefinition && definition.Url is not null:
                    Destination(text, definition.UrlSpan, definition.Url, part, edits);
                    break;
                case Footnote footnote when part.LabelSuffix is not null:
                    labelsComplete &= Label(text, footnote.LabelSpan, footnote.Label, part.LabelSuffix, labels);
                    break;
                case FootnoteLink { IsBackLink: false } reference when part.LabelSuffix is not null:
                    labelsComplete &= Label(text, reference.Span, reference.Footnote?.Label, part.LabelSuffix, labels);
                    break;
                case FencedCodeBlock { ClosingFencedCharCount: 0, OpeningFencedCharCount: > 0 } fence:
                    // An unclosed fence would swallow every following part.
                    closeFence = new string(fence.FencedChar, fence.OpeningFencedCharCount);
                    break;
            }
        }
        if (labelsComplete) edits.AddRange(labels);

        var builder = new System.Text.StringBuilder(text);
        int limit = int.MaxValue;
        foreach (var edit in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.Length))
        {
            if (edit.Start + edit.Length > limit) continue;
            builder.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
            limit = edit.Start;
        }
        if (closeFence is not null) builder.Append('\n').Append(closeFence).Append('\n');
        return builder.ToString();
    }

    private static void Destination(string text, SourceSpan span, string url, MarkdownMergePart part, List<Edit> edits)
    {
        if (!Valid(text, span)) return;
        int start = span.Start, length = span.Length;
        string raw = text.Substring(start, length);
        if (raw.Length >= 2 && raw[0] == '<' && raw[^1] == '>') { start++; length -= 2; raw = raw[1..^1]; }
        if (!string.Equals(raw, url, StringComparison.Ordinal)) return;
        string? mapped = part.MapUrl(url);
        if (mapped is not null && !string.Equals(mapped, url, StringComparison.Ordinal)) edits.Add(new(start, length, mapped));
    }

    private static bool Label(string text, SourceSpan span, string? label, string suffix, List<Edit> edits)
    {
        if (string.IsNullOrEmpty(label) || !Valid(text, span)) return false;
        int at = text.IndexOf(label, span.Start, span.Length, StringComparison.Ordinal);
        if (at < 0) return false;
        edits.Add(new(at + label.Length, 0, suffix));
        return true;
    }

    private static bool Valid(string text, SourceSpan span) => !span.IsEmpty && span.Start >= 0 && span.Length > 0 && span.Start + span.Length <= text.Length;

    private sealed record Edit(int Start, int Length, string Text);
}
