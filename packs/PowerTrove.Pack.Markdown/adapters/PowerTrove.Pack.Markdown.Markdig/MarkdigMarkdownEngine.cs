using AngleSharp.Dom;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;

namespace PowerTrove.Pack.Markdown.Markdig;

/// <summary>
/// Markdig renders CommonMark with the GitHub-style extensions; <see cref="MarkdownHtmlSanitizer"/> then reduces
/// the result, including raw HTML written in the Markdown, to static allowlisted markup.
/// </summary>
public sealed class MarkdigMarkdownEngine : IMarkdownEngine
{
    /// <summary>Characters of Markdown rendered in one call.</summary>
    public const int MaximumMarkdownLength = 4 * 1024 * 1024;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseFootnotes()
        .UseDefinitionLists()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .Build();

    public RenderedMarkdown Render(string markdown, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (markdown.Length > MaximumMarkdownLength) throw new MarkdownLimitException("Markdown exceeds the rendering limit.");
        cancellationToken.ThrowIfCancellationRequested();
        string html = global::Markdig.Markdown.ToHtml(markdown.Replace("\0", "�", StringComparison.Ordinal), Pipeline);
        cancellationToken.ThrowIfCancellationRequested();
        var sanitized = MarkdownHtmlSanitizer.Sanitize(html, cancellationToken);
        return new Rendered(sanitized);
    }

    public string PrepareMergePart(string markdown, MarkdownMergePart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(part);
        if (markdown.Length > MaximumMarkdownLength) throw new MarkdownLimitException("Markdown exceeds the rendering limit.");
        return MarkdownMergeRewriter.Rewrite(markdown, part, cancellationToken);
    }

    public HtmlConversion ConvertHtml(string html, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (html.Length > MaximumMarkdownLength * 2) throw new MarkdownLimitException("HTML exceeds the conversion limit.");
        return new HtmlMarkdownConverter().Convert(html, cancellationToken);
    }

    private sealed class Rendered(SanitizedHtml html) : RenderedMarkdown
    {
        public override IReadOnlyList<string> ImageSources => html.ImageSources;
        public override string? Title => html.Title;
        public override int RemovedContent => html.Removed;
        public override string ToHtml(Func<string, string?> mapImage, CancellationToken cancellationToken, string? anchorPrefix = null)
        {
            ArgumentNullException.ThrowIfNull(mapImage);
            var body = (IElement)html.Body.Clone(true);
            if (!string.IsNullOrEmpty(anchorPrefix))
            {
                foreach (var element in body.QuerySelectorAll("[id]")) element.Id = anchorPrefix + element.Id;
                foreach (var link in body.QuerySelectorAll("a[href^='#']"))
                    if (link.GetAttribute("href") is { Length: > 1 } target) link.SetAttribute("href", "#" + anchorPrefix + target[1..]);
            }
            foreach (var image in body.QuerySelectorAll("img").ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = image.GetAttribute(MarkdownHtmlSanitizer.SourceAttribute) ?? string.Empty;
                image.RemoveAttribute(MarkdownHtmlSanitizer.SourceAttribute);
                string? mapped = source.Length == 0 ? null : mapImage(source);
                if (mapped is not null && MarkdownHtmlSanitizer.IsEmittableImageSource(mapped)) { image.SetAttribute("src", mapped); continue; }
                var placeholder = image.Owner!.CreateElement("span");
                placeholder.ClassName = "image-missing";
                string alt = image.GetAttribute("alt") ?? string.Empty;
                placeholder.TextContent = string.IsNullOrWhiteSpace(alt) ? "\U0001F5BC" : alt;
                image.Replace(placeholder);
            }
            return body.InnerHtml;
        }
    }
}
