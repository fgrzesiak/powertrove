using FileKit.Html;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Pack.Markdown.Markdig;

[assembly: ActionPackFactory(typeof(PowerTrove.Pack.Markdown.MarkdownPackFactory))]

namespace PowerTrove.Pack.Markdown;

public sealed class MarkdownPackFactory : IPackFactory
{
    public ActionPackInfo Info => MarkdownPack.Describe();
    public IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        // The pack brings its own printer: its validator accepts the pack's style sheet, which the PDF pack's
        // validator is not obliged to. Hosts may still supply an engine (tests do).
        return new MarkdownPack(host.Files, host.GetService<IMarkdownEngine>() ?? new MarkdigMarkdownEngine(),
            OperatingSystem.IsWindows() ? new MarkdownPdfPrinter() : null, host.GetService<IPdfTextReader>() ?? new PowerTrove.Pack.Markdown.Pdf.PdfLayoutTextReader());
    }
}
