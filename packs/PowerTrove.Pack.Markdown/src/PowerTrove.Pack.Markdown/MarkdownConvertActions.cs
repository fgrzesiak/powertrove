using System.Globalization;
using System.Text;
using FileKit.Html;
using FileKit.Text;

namespace PowerTrove.Pack.Markdown;

public sealed record MarkdownHtmlOptions(bool EmbedImages, bool Combine = false, string Order = "", bool Divider = false);

/// <summary>Markdown to a standalone, static HTML page next to the source, or one page for all chosen files.</summary>
public sealed class MarkdownToHtmlAction(FileActionServices services, IMarkdownEngine? engine) : PerFileAction<MarkdownHtmlOptions>(services)
{
    public override ActionDescriptor Descriptor { get; } = new("powertrove.markdown.to-html", SR.Get("ToHtmlTitle"), SR.Get("ToHtmlDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["markdown", "md", "html", "web", "page", "convert", "export", "readme", "merge", "combine"], true, true,
        Parameters: [ActionParameterDefinition.Choice("Images", SR.Get("ImagesOption"), "embed",
            [new("embed", SR.Get("ImagesEmbed")), new("link", SR.Get("ImagesLink"))]), .. CombineParameters.Html],
        Selection: SelectionRequirements.Any("text", "other"));
    protected override FileInputFilter Input => new(SR.Get("MarkdownFiles"), MarkdownDocuments.MarkdownExtensions, null);
    protected override string? UnavailableReason => engine is null ? SR.Get("EngineUnavailable") : null;
    protected override MarkdownHtmlOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetChoice("Images") == "embed", values.GetBoolean("Combine"), values.GetText("Order"), values.GetBoolean("Divider"));
    protected override string? ValidateOptions(MarkdownHtmlOptions options) => FileOrderParameter.IsValid(options.Order) ? null : SR.Get("MergeOrderInvalid");
    protected override string ProgressVerb => SR.Get("Converting");
    protected override string DescribeOperation(int count, MarkdownHtmlOptions options) => options.Combine && count > 1 ? SR.Get("MergeOperation_Html") :
        SR.Count("ToHtmlOperation", count);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, MarkdownHtmlOptions options) =>
        [SR.Get("SanitizedNotice"), SR.Get(options.EmbedImages ? "HtmlEmbedNotice" : "HtmlLinkNotice"), .. MarkdownMerge.Tip(inputs, options.Combine)];
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) =>
        MarkdownMerge.WithChoices(await base.PreviewAsync(context, parameters, cancellationToken).ConfigureAwait(false), context, Input);
    protected override IReadOnlyList<string> PlanExpectedOutputs(IReadOnlyList<SelectedFile> inputs, MarkdownHtmlOptions options) =>
        options.Combine && inputs.Count > 1 ? MarkdownMerge.Plan(Services, inputs, options.Order, ".html") : base.PlanExpectedOutputs(inputs, options);
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, MarkdownHtmlOptions options)
    {
        yield return new(Path.GetFileNameWithoutExtension(input.Name), ".html");
    }
    protected override Task RunAsync(IReadOnlyList<SelectedFile> inputs, MarkdownHtmlOptions options, FileOutputWriter output, List<ActionFailure> failures,
        ExecutionStatistics statistics, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
    {
        if (!options.Combine || inputs.Count < 2) return base.RunAsync(inputs, options, output, failures, statistics, progress, cancellationToken);
        return MarkdownMerge.RunCombinedAsync(inputs, PrepareAsync, async (items, step) =>
        {
            var ordered = MarkdownMerge.Ordered(items, i => i.File, options.Order);
            var documents = new MarkdownDocuments(Services.FileSystem, engine!);
            var texts = await MarkdownMerge.ReadAllAsync(documents, ordered, cancellationToken).ConfigureAwait(false);
            await MarkdownMerge.WriteHtmlAsync(documents, ordered, texts, options.EmbedImages, options.Divider, MarkdownMerge.OutputName(ordered[0].File), output, step, cancellationToken).ConfigureAwait(false);
        }, DescribeException, failures, progress, cancellationToken);
    }
    protected override async Task ProcessAsync(FileWorkItem item, MarkdownHtmlOptions options, FileOutputWriter output, CancellationToken ct)
    {
        var documents = new MarkdownDocuments(Services.FileSystem, engine!);
        string markdown = await documents.ReadTextAsync(item.Path, MarkdownDocuments.MaximumMarkdownBytes, ct).ConfigureAwait(false);
        BuiltDocument built;
        try { built = await documents.BuildAsync(markdown, item.Path, options.EmbedImages ? ImageMode.Embed : ImageMode.Link, MarkdownDocuments.HtmlBudget, ct, keepRemote: true).ConfigureAwait(false); }
        catch (MarkdownLimitException) { throw new FileProcessingException(SR.Get("TooComplex")); }
        await MarkdownMerge.WriteTextAsync(output, item.Directory, item.BaseName, ".html", MarkdownDocuments.ExportDocument(built), ct).ConfigureAwait(false);
    }
}

public sealed record MarkdownPdfOptions(HtmlPrintOptions Print, bool Combine = false, string Order = "", bool Divider = false, bool PageBreaks = true);

/// <summary>Markdown to PDF through the installed Microsoft Edge, with local images embedded; optionally one PDF for all chosen files.</summary>
public sealed class MarkdownToPdfAction(FileActionServices services, IMarkdownEngine? engine, ILocalHtmlPdfPrinter? printer) : PerFileAction<MarkdownPdfOptions>(services)
{
    public override ActionDescriptor Descriptor { get; } = new("powertrove.markdown.to-pdf", SR.Get("ToPdfTitle"), SR.Get("ToPdfDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["markdown", "md", "pdf", "print", "convert", "export", "readme", "merge", "combine"], true, true,
        Parameters: [ActionParameterDefinition.Choice("Paper", SR.Get("Paper"), "a4", [new("a4", SR.Get("PaperA4")), new("letter", SR.Get("PaperLetter"))]),
            ActionParameterDefinition.Boolean("Landscape", SR.Get("Landscape"), false), ActionParameterDefinition.Integer("MarginMm", SR.Get("Margin"), 15, 0, 50),
            .. CombineParameters.Pdf],
        Selection: SelectionRequirements.Any("text", "other"));
    protected override FileInputFilter Input => new(SR.Get("MarkdownFiles"), MarkdownDocuments.MarkdownExtensions, null);
    protected override int MaximumInputs => 100;
    protected override string? UnavailableReason => engine is null ? SR.Get("EngineUnavailable") : printer is { IsAvailable: true } ? null : SR.Get("PrinterUnavailable");
    protected override MarkdownPdfOptions ReadOptions(ActionParameterValues values) =>
        new(new(values.GetChoice("Paper"), values.GetBoolean("Landscape"), values.GetInt32("MarginMm")),
            values.GetBoolean("Combine"), values.GetText("Order"), values.GetBoolean("Divider"), values.GetBoolean("PageBreaks"));
    protected override string? ValidateOptions(MarkdownPdfOptions options) => FileOrderParameter.IsValid(options.Order) ? null : SR.Get("MergeOrderInvalid");
    protected override string ProgressVerb => SR.Get("Converting");
    protected override string DescribeOperation(int count, MarkdownPdfOptions options) => options.Combine && count > 1 ? SR.Get("MergeOperation_Pdf") :
        SR.Count("ToPdfOperation", count);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, MarkdownPdfOptions options) =>
        [SR.Get("SanitizedNotice"), SR.Get("PdfNotice"), .. MarkdownMerge.Tip(inputs, options.Combine)];
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) =>
        MarkdownMerge.WithChoices(await base.PreviewAsync(context, parameters, cancellationToken).ConfigureAwait(false), context, Input);
    protected override IReadOnlyList<string> PlanExpectedOutputs(IReadOnlyList<SelectedFile> inputs, MarkdownPdfOptions options) =>
        options.Combine && inputs.Count > 1 ? MarkdownMerge.Plan(Services, inputs, options.Order, ".pdf") : base.PlanExpectedOutputs(inputs, options);
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, MarkdownPdfOptions options)
    {
        yield return new(Path.GetFileNameWithoutExtension(input.Name), ".pdf");
    }
    protected override Task RunAsync(IReadOnlyList<SelectedFile> inputs, MarkdownPdfOptions options, FileOutputWriter output, List<ActionFailure> failures,
        ExecutionStatistics statistics, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
    {
        if (!options.Combine || inputs.Count < 2) return base.RunAsync(inputs, options, output, failures, statistics, progress, cancellationToken);
        return MarkdownMerge.RunCombinedAsync(inputs, PrepareAsync, async (items, step) =>
        {
            var ordered = MarkdownMerge.Ordered(items, i => i.File, options.Order);
            var documents = new MarkdownDocuments(Services.FileSystem, engine!);
            var texts = await MarkdownMerge.ReadAllAsync(documents, ordered, cancellationToken).ConfigureAwait(false);
            await MarkdownMerge.WritePdfAsync(documents, printer!, ordered, texts, options.Divider, options.PageBreaks, options.Print,
                MarkdownMerge.OutputName(ordered[0].File), output, step, cancellationToken).ConfigureAwait(false);
        }, DescribeException, failures, progress, cancellationToken);
    }
    protected override async Task ProcessAsync(FileWorkItem item, MarkdownPdfOptions options, FileOutputWriter output, CancellationToken ct)
    {
        var documents = new MarkdownDocuments(Services.FileSystem, engine!);
        string markdown = await documents.ReadTextAsync(item.Path, MarkdownDocuments.MaximumMarkdownBytes, ct).ConfigureAwait(false);
        try
        {
            var built = await documents.BuildAsync(markdown, item.Path, ImageMode.Embed, MarkdownDocuments.PrintBudget(markdown), ct).ConfigureAwait(false);
            using var source = new MemoryStream(Encoding.UTF8.GetBytes(MarkdownDocuments.PrintDocument(built)));
            var document = await printer!.PrepareAsync(source, ct).ConfigureAwait(false);
            await output.WriteFileFromPathAsync(item.Directory, item.BaseName, ".pdf", (path, token) => printer.RenderAsync(document, path, options.Print, token), ct).ConfigureAwait(false);
        }
        catch (MarkdownLimitException) { throw new FileProcessingException(SR.Get("TooComplex")); }
        catch (HtmlPrintException ex) { throw new FileProcessingException(SR.Get(ex.Failure == HtmlPrintFailure.Cleanup ? "PdfCleanupFailed" : "PdfFailed"), ex); }
    }
}

/// <summary>The "combine into one file" options of the conversions; order and layout show only when combining.</summary>
internal static class CombineParameters
{
    private static ActionParameterDefinition Combine => ActionParameterDefinition.Boolean("Combine", SR.Get("CombineOption"), false, SR.Get("CombineHelp"));
    private static ActionParameterDefinition Order => ActionParameterDefinition.FileOrder("Order", SR.Get("MergeOrder"), SR.Get("MergeOrderHelp")).VisibleWhen("Combine", "true");
    private static ActionParameterDefinition Divider => ActionParameterDefinition.Boolean("Divider", SR.Get("MergeDivider"), false).VisibleWhen("Combine", "true");
    public static ActionParameterDefinition[] Html => [Combine, Order, Divider];
    public static ActionParameterDefinition[] Pdf => [Combine, Order, Divider, ActionParameterDefinition.Boolean("PageBreaks", SR.Get("MergePageBreaks"), true).VisibleWhen("Combine", "true")];
}
