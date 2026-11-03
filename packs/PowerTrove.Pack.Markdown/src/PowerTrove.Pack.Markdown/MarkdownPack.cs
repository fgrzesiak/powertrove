using FileKit.Html;

namespace PowerTrove.Pack.Markdown;

/// <summary>Markdown viewer and editor previews plus Markdown, HTML and PDF conversions, merging and conversion of documents to Markdown.</summary>
public sealed class MarkdownPack : IActionPack
{
    public MarkdownPack(FileActionServices services, IMarkdownEngine? engine, ILocalHtmlPdfPrinter? printer = null, IPdfTextReader? pdfReader = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        Actions = new IAction[]
        {
            new MarkdownViewAction(services, engine, edit: false),
            new MarkdownViewAction(services, engine, edit: true),
            new MarkdownToHtmlAction(services, engine),
            new MarkdownToPdfAction(services, engine, printer),
            new MergeMarkdownAction(services, engine, printer),
            new ToMarkdownAction(services, engine, pdfReader)
        };
    }

    public ActionPackInfo Info { get; } = Describe();
    internal static ActionPackInfo Describe() => new("powertrove.markdown", SR.Get("PackTitle"), PowerTrove.Packs.PackReleaseVersion.Read(typeof(MarkdownPack).Assembly), "PowerTrove");
    public IReadOnlyList<IAction> Actions { get; }
}
