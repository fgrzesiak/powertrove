namespace PowerTrove.Pack.Markdown;

/// <summary>
/// Markdown and HTML conversion port. The implementation parses untrusted text and returns only static,
/// sanitized markup: no scripts, event handlers, frames, forms, styles or embedded objects. It never reads
/// files or the network; the pack resolves images through <see cref="RenderedMarkdown.ToHtml"/>.
/// </summary>
public interface IMarkdownEngine
{
    /// <summary>CommonMark plus GitHub-style tables, task lists, strikethrough, autolinks and footnotes.</summary>
    RenderedMarkdown Render(string markdown, CancellationToken cancellationToken);

    /// <summary>
    /// Prepares one document for a merged Markdown file: link and image destinations pass through
    /// <see cref="MarkdownMergePart.MapUrl"/>, footnote labels get <see cref="MarkdownMergePart.LabelSuffix"/> so they
    /// do not collide with other parts, and YAML front matter is dropped unless kept. Everything else stays as written.
    /// </summary>
    string PrepareMergePart(string markdown, MarkdownMergePart part, CancellationToken cancellationToken);

    /// <summary>Converts static HTML to Markdown. Active and unsupported content is dropped.</summary>
    HtmlConversion ConvertHtml(string html, CancellationToken cancellationToken);
}

/// <summary>Sanitized Markdown rendering whose image sources the caller still maps.</summary>
public abstract class RenderedMarkdown
{
    /// <summary>Distinct image sources as written in the document, in document order.</summary>
    public abstract IReadOnlyList<string> ImageSources { get; }

    /// <summary>Text of the first level-one heading, if any.</summary>
    public abstract string? Title { get; }

    /// <summary>Raw HTML elements or attributes the sanitizer removed.</summary>
    public abstract int RemovedContent { get; }

    /// <summary>
    /// Serializes the body markup. <paramref name="mapImage"/> returns the source to emit for an original source,
    /// or null to replace the image with its alternative text.
    /// </summary>
    /// <param name="anchorPrefix">Prefixes element ids and in-page links, so parts of a combined page keep their own anchors.</param>
    public abstract string ToHtml(Func<string, string?> mapImage, CancellationToken cancellationToken, string? anchorPrefix = null);
}

/// <param name="MapUrl">Returns the destination to write for a link or image destination, or null to keep it.</param>
public sealed record MarkdownMergePart(Func<string, string?> MapUrl, string? LabelSuffix, bool KeepFrontMatter);

public sealed record HtmlConversion(string Markdown, int OmittedImages, int RemovedContent)
{
    public override string ToString() => $"HtmlConversion {{ OmittedImages = {OmittedImages}, RemovedContent = {RemovedContent} }}";
}

/// <summary>Raised for input that exceeds the engine's structural limits.</summary>
public sealed class MarkdownLimitException(string message) : Exception(message);
