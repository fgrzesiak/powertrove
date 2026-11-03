using System.Text;
using System.Text.RegularExpressions;
using FileKit.Html;

namespace PowerTrove.Pack.Markdown;

public sealed record MarkdownMergeOptions(string Format, string Order, bool Divider, bool PageBreaks, bool EmbedImages, HtmlPrintOptions Print);

/// <summary>
/// Joins several Markdown files, in an order the user can arrange, into one Markdown, HTML or PDF file.
/// Markdown output keeps every part as written; only relative link and image destinations are rebased to
/// the new file's folder and footnote labels are made unique. HTML and PDF render every part on its own,
/// so images resolve next to their own file and anchors stay apart.
/// </summary>
public sealed partial class MergeMarkdownAction(FileActionServices services, IMarkdownEngine? engine, ILocalHtmlPdfPrinter? printer)
    : CombineFilesAction<MarkdownMergeOptions>(services)
{
    public const string Id = "powertrove.markdown.merge";

    public override ActionDescriptor Descriptor { get; } = new(Id, SR.Get("MergeTitle"), SR.Get("MergeDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["markdown", "md", "merge", "combine", "join", "concatenate", "order", "html", "pdf", "book"], true, true,
        Parameters:
        [
            ActionParameterDefinition.Choice("Format", SR.Get("MergeFormat"), "md",
                [new("md", SR.Get("MergeFormatMarkdown")), new("html", SR.Get("MergeFormatHtml")), new("pdf", SR.Get("MergeFormatPdf"))]),
            ActionParameterDefinition.FileOrder("Order", SR.Get("MergeOrder"), SR.Get("MergeOrderHelp")),
            ActionParameterDefinition.Boolean("Divider", SR.Get("MergeDivider"), false),
            ActionParameterDefinition.Boolean("PageBreaks", SR.Get("MergePageBreaks"), true).VisibleWhen("Format", "pdf"),
            ActionParameterDefinition.Choice("Images", SR.Get("ImagesOption"), "embed",
                [new("embed", SR.Get("ImagesEmbed")), new("link", SR.Get("ImagesLink"))]).VisibleWhen("Format", "html"),
            ActionParameterDefinition.Choice("Paper", SR.Get("Paper"), "a4", [new("a4", SR.Get("PaperA4")), new("letter", SR.Get("PaperLetter"))]).VisibleWhen("Format", "pdf"),
            ActionParameterDefinition.Boolean("Landscape", SR.Get("Landscape"), false).VisibleWhen("Format", "pdf"),
            ActionParameterDefinition.Integer("MarginMm", SR.Get("Margin"), 15, 0, 50).VisibleWhen("Format", "pdf")
        ],
        Selection: SelectionRequirements.Multiple("text", "other"));

    protected override FileInputFilter Input => new(SR.Get("MarkdownFiles"), MarkdownDocuments.MarkdownExtensions, null);
    protected override int MaximumInputs => 100;
    protected override string? UnavailableReason => engine is null ? SR.Get("EngineUnavailable") : null;

    protected override MarkdownMergeOptions ReadOptions(ActionParameterValues values) => new(values.GetChoice("Format"), values.GetText("Order"),
        values.GetBoolean("Divider"), values.GetBoolean("PageBreaks"), values.GetChoice("Images") == "embed",
        new(values.GetChoice("Paper"), values.GetBoolean("Landscape"), values.GetInt32("MarginMm")));

    protected override string? ValidateOptions(MarkdownMergeOptions options) =>
        !FileOrderParameter.IsValid(options.Order) ? SR.Get("MergeOrderInvalid")
        : options.Format == "pdf" && printer is not { IsAvailable: true } ? SR.Get("PrinterUnavailable") : null;

    protected override string DescribeOperation(int count, MarkdownMergeOptions options) => SR.Get(options.Format switch
    {
        "html" => "MergeOperation_Html",
        "pdf" => "MergeOperation_Pdf",
        _ => "MergeOperation_Markdown"
    });

    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, MarkdownMergeOptions options) => options.Format switch
    {
        "html" => [SR.Get("SanitizedNotice"), SR.Get(options.EmbedImages ? "HtmlEmbedNotice" : "HtmlLinkNotice")],
        "pdf" => [SR.Get("SanitizedNotice"), SR.Get("PdfNotice")],
        _ => [SR.Get("MergeMarkdownNotice")]
    };

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        var preview = await base.PreviewAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (context.Content is not FileSelection selection) return preview;
        var files = OrderInputs(selection.Files.Where(Input.Accepts));
        return preview with { ParameterChoices = [MarkdownMerge.Choices(files)] };
    }

    /// <summary>The output goes next to the file the user put first.</summary>
    protected override IReadOnlyList<string> PlanExpectedOutputs(IReadOnlyList<SelectedFile> inputs, MarkdownMergeOptions options) =>
        base.PlanExpectedOutputs(Ordered(inputs, f => f, options), options);

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, MarkdownMergeOptions options) =>
        new(MarkdownMerge.OutputName(inputs[0]), options.Format switch { "html" => ".html", "pdf" => ".pdf", _ => ".md" });

    private static IReadOnlyList<T> Ordered<T>(IReadOnlyList<T> items, Func<T, SelectedFile> file, MarkdownMergeOptions options) =>
        MarkdownMerge.Ordered(items, file, options.Order);

    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, MarkdownMergeOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var ordered = Ordered(items, i => i.File, options);
        var documents = new MarkdownDocuments(Services.FileSystem, engine!);
        var texts = await MarkdownMerge.ReadAllAsync(documents, ordered, cancellationToken).ConfigureAwait(false);
        string name = MarkdownMerge.OutputName(ordered[0].File);
        await MarkdownMerge.MapErrors(options.Format switch
        {
            "html" => () => MarkdownMerge.WriteHtmlAsync(documents, ordered, texts, options.EmbedImages, options.Divider, name, output, progress, cancellationToken),
            "pdf" => () => MarkdownMerge.WritePdfAsync(documents, printer!, ordered, texts, options.Divider, options.PageBreaks, options.Print, name, output, progress, cancellationToken),
            _ => () => WriteMarkdownAsync(ordered, texts, options, name, output, progress, cancellationToken)
        }).ConfigureAwait(false);
    }

    private async Task WriteMarkdownAsync(IReadOnlyList<FileWorkItem> items, IReadOnlyList<string> texts, MarkdownMergeOptions options,
        string name, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct)
    {
        string target = Services.OutputDirectoryOverride.Value ?? items[0].Directory;
        string newLine = texts[0].Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var merged = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            string source = items[i].Directory;
            var part = new MarkdownMergePart(url => Rebase(url, source, target), i == 0 ? null : "-" + (i + 1), KeepFrontMatter: i == 0);
            string text = await Task.Run(() => engine!.PrepareMergePart(texts[i], part, ct), ct).ConfigureAwait(false);
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim('\n');
            if (i > 0) merged.Append(options.Divider ? "\n\n---\n\n" : "\n\n");
            merged.Append(text);
            progress?.Report(0.9 * (i + 1) / items.Count);
        }
        merged.Append('\n');
        string content = newLine == "\n" ? merged.ToString() : merged.ToString().Replace("\n", newLine, StringComparison.Ordinal);
        await MarkdownMerge.WriteTextAsync(output, items[0].Directory, name, ".md", content, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A relative destination rewritten for the merged file's folder, or null to keep it: absolute addresses,
    /// in-page anchors and destinations on another drive stay as written.
    /// </summary>
    internal static string? Rebase(string url, string sourceDirectory, string targetDirectory)
    {
        string value = url.Trim();
        if (value.Length == 0 || value[0] is '#' or '/' or '\\' || Scheme().IsMatch(value)) return null;
        if (string.Equals(Path.GetFullPath(sourceDirectory).TrimEnd('\\', '/'), Path.GetFullPath(targetDirectory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
        int cut = value.IndexOfAny(['?', '#']);
        string path = cut >= 0 ? value[..cut] : value, suffix = cut >= 0 ? value[cut..] : string.Empty;
        string relative;
        try
        {
            string full = Path.GetFullPath(Path.Combine(sourceDirectory, Uri.UnescapeDataString(path).Replace('/', Path.DirectorySeparatorChar)));
            relative = Path.GetRelativePath(targetDirectory, full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException) { return null; }
        if (Path.IsPathRooted(relative)) return null;
        var builder = new StringBuilder();
        foreach (char c in relative.Replace(Path.DirectorySeparatorChar, '/'))
            builder.Append(c is ' ' or '%' or '(' or ')' or '<' or '>' or '"' || char.IsControl(c) ? Uri.EscapeDataString(c.ToString()) : c.ToString());
        if (path.EndsWith('/') && builder.Length > 0 && builder[^1] != '/') builder.Append('/');
        return builder + suffix;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Scheme();
}
