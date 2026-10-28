using AngleSharp.Html.Parser;
using FileKit.Html;

namespace PowerTrove.Pack.EInvoice.Html;

/// <summary>
/// Prints generated invoice HTML through installed Edge, so the pack does not depend on a host
/// or another pack for its PDF output. Hosts may still supply their own printer.
/// </summary>
public sealed class InvoiceHtmlPdfPrinter : ILocalHtmlPdfPrinter
{
    private readonly ChromiumPdfWriter _writer = new(new WindowsHtmlBrowserLauncher());
    private readonly IHtmlMarkupValidator _validator = new PassiveHtmlMarkupValidator();
    public bool IsAvailable => _writer.IsAvailable;
    public Task<HtmlPrintDocument> PrepareAsync(Stream source, CancellationToken cancellationToken) => HtmlPrintDocument.ReadAsync(source, _validator, cancellationToken);
    public Task RenderAsync(HtmlPrintDocument document, string destinationPath, HtmlPrintOptions options, CancellationToken cancellationToken) =>
        _writer.WriteAsync(document, destinationPath, options, cancellationToken);
}

/// <summary>Same passive-markup policy as the PDF pack's HTML printing: no active, navigating or foreign content.</summary>
internal sealed class PassiveHtmlMarkupValidator : IHtmlMarkupValidator
{
    private static readonly string[] Unsupported = ["script", "iframe", "frame", "frameset", "object", "embed", "base", "template", "svg", "math", "link", "form", "input", "textarea", "select", "button", "audio", "video", "source", "track"];
    public string ValidateAndNormalize(string html)
    {
        // A standalone parser has no resource loader or script engine. Serialize its
        // parsed DOM so validation and browser input share the same interpretation.
        int tags = 0;
        var parserOptions = new HtmlParserOptions { IsScripting = false, OnToken = (token, _) =>
        {
            if (token.Type != HtmlTokenType.StartTag) return;
            if (++tags > 50000 || Unsupported.Any(token.IsStartTag))
                throw new HtmlPrintException("Active HTML or foreign content is unsupported.");
        } };
        using var document = new HtmlParser(parserOptions).ParseDocument(html);
        int count = 0;
        foreach (var element in document.All)
        {
            if (++count > 50000 || element.NamespaceUri != "http://www.w3.org/1999/xhtml" ||
                element.LocalName is "script" or "iframe" or "frame" or "frameset" or "object" or "embed" or "base" or "template" or "link" ||
                element.LocalName == "meta" && element.HasAttribute("http-equiv") ||
                element.Attributes.Any(a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || a.Name.Equals("srcdoc", StringComparison.OrdinalIgnoreCase)))
                throw new HtmlPrintException("Active HTML, automatic navigation or foreign content is unsupported.");
            int depth = 0;
            for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
                if (++depth > 128) throw new HtmlPrintException("HTML nesting limit exceeded.");
        }
        return document.DocumentElement.OuterHtml;
    }
}
