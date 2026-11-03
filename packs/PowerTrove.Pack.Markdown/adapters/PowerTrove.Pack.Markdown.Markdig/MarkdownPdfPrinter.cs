using AngleSharp.Html.Parser;
using FileKit.Html;

namespace PowerTrove.Pack.Markdown.Markdig;

/// <summary>
/// Prints the pack's sanitized Markdown documents through the installed Microsoft Edge, so the pack depends on
/// neither the host nor the PDF pack. The validator checks the document again before Edge receives it.
/// </summary>
public sealed class MarkdownPdfPrinter : ILocalHtmlPdfPrinter
{
    private readonly ChromiumPdfWriter _writer = new(new WindowsHtmlBrowserLauncher());
    private readonly IHtmlMarkupValidator _validator = new MarkdownPrintValidator();
    public bool IsAvailable => _writer.IsAvailable;
    public Task<HtmlPrintDocument> PrepareAsync(Stream source, CancellationToken cancellationToken) => HtmlPrintDocument.ReadAsync(source, _validator, cancellationToken);
    public Task RenderAsync(HtmlPrintDocument document, string destinationPath, HtmlPrintOptions options, CancellationToken cancellationToken) =>
        _writer.WriteAsync(document, destinationPath, options, cancellationToken);
}

/// <summary>Accepts only the pack's own static output: its style sheet plus sanitized, passive markup.</summary>
internal sealed class MarkdownPrintValidator : IHtmlMarkupValidator
{
    private static readonly string[] Rejected = ["script", "iframe", "frame", "frameset", "object", "embed", "base", "template", "link", "meta", "form",
        "input", "textarea", "select", "button", "audio", "video", "source", "track", "svg", "math", "noscript", "canvas"];

    public string ValidateAndNormalize(string html)
    {
        var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument(html);
        int count = 0;
        foreach (var element in document.All)
        {
            if (++count > 200_000 || element.NamespaceUri != "http://www.w3.org/1999/xhtml" || Rejected.Contains(element.LocalName, StringComparer.OrdinalIgnoreCase)
                || element.Attributes.Any(a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || a.Name.Equals("srcdoc", StringComparison.OrdinalIgnoreCase)
                    || a.Name.Equals("style", StringComparison.OrdinalIgnoreCase))
                || element.LocalName == "img" && !(element.GetAttribute("src") ?? string.Empty).StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && element.HasAttribute("src"))
                throw new HtmlPrintException("Active, external or foreign content is unsupported.");
        }
        return document.Head!.InnerHtml + document.Body!.InnerHtml;
    }
}
