using System.Text;
using FileKit.Html;

namespace PowerTrove.Pack.Markdown;

/// <summary>
/// Joins several Markdown files into one HTML page or PDF, shared by Merge Markdown files and the
/// "combine into one file" option of the conversions. Every part renders on its own, so images resolve next to
/// their own file and anchors get a per-part prefix; all parts share one image budget.
/// </summary>
internal static class MarkdownMerge
{
    /// <summary>Characters of Markdown joined in one run.</summary>
    public const int MaximumMergedLength = 4 * 1024 * 1024;
    /// <summary>Files combined in one run.</summary>
    public const int MaximumParts = 100;

    /// <summary>The user's order and subset; fewer than two files is not a merge.</summary>
    public static IReadOnlyList<T> Ordered<T>(IReadOnlyList<T> items, Func<T, SelectedFile> file, string order)
    {
        var ordered = FileOrderParameter.Apply(items, file, order);
        return ordered.Count >= 2 ? ordered : throw new FileProcessingException(SR.Get("MergeNeedsTwo"));
    }

    public static string OutputName(SelectedFile first) => Path.GetFileNameWithoutExtension(first.Name) + " (merged)";

    public static ActionParameterChoiceSet Choices(IReadOnlyList<SelectedFile> files) => FileOrderParameter.Choices("Order", files, SR.Get("MergeOrderHelp"));

    /// <summary>Names only appear in the dialog's choices, and only when there is something to order.</summary>
    public static ActionPreview WithChoices(ActionPreview preview, ContextSnapshot context, FileInputFilter input)
    {
        if (context.Content is not FileSelection selection) return preview;
        var files = NaturalFileNameComparer.Sort(selection.Files.Where(input.Accepts));
        return files.Count < 2 ? preview : preview with { ParameterChoices = [Choices(files)] };
    }

    /// <summary>One output next to the file the user put first.</summary>
    public static IReadOnlyList<string> Plan(FileActionServices services, IReadOnlyList<SelectedFile> inputs, string order, string extension)
    {
        if (inputs.Count > MaximumParts) throw new FileProcessingException(SR.Get("MergeTooMany"));
        var first = Ordered(inputs, f => f, order)[0];
        string directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(first.Path!)) ?? string.Empty;
        return directory.Length == 0 ? [] : [services.Planner.PlanFile(directory, OutputName(first), extension).TargetPath];
    }

    /// <summary>Points users who selected several files to the option that makes one file.</summary>
    public static IEnumerable<string> Tip(IReadOnlyList<SelectedFile> inputs, bool combine) => inputs.Count > 1 && !combine ? [SR.Get("MergeTip")] : [];

    public static async Task<IReadOnlyList<string>> ReadAllAsync(MarkdownDocuments documents, IReadOnlyList<FileWorkItem> items, CancellationToken ct)
    {
        var texts = new List<string>();
        long length = 0;
        foreach (var item in items)
        {
            string text = await documents.ReadTextAsync(item.Path, MarkdownDocuments.MaximumMarkdownBytes, ct).ConfigureAwait(false);
            if ((length += text.Length) > MaximumMergedLength) throw new FileProcessingException(SR.Get("MergeTooLarge"));
            texts.Add(text);
        }
        return texts;
    }

    public static async Task MapErrors(Func<Task> write)
    {
        try { await write().ConfigureAwait(false); }
        catch (MarkdownLimitException) { throw new FileProcessingException(SR.Get("TooComplex")); }
        catch (HtmlPrintException ex) { throw new FileProcessingException(SR.Get(ex.Failure == HtmlPrintFailure.Cleanup ? "PdfCleanupFailed" : "PdfFailed"), ex); }
    }

    public static async Task WriteHtmlAsync(MarkdownDocuments documents, IReadOnlyList<FileWorkItem> items, IReadOnlyList<string> texts, bool embedImages, bool divider,
        string name, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct)
    {
        var built = await BuildAsync(documents, items, texts, embedImages ? ImageMode.Embed : ImageMode.Link, MarkdownDocuments.HtmlBudget, keepRemote: true, divider, progress, ct).ConfigureAwait(false);
        await WriteTextAsync(output, items[0].Directory, name, ".html", MarkdownDocuments.ExportDocument(built), ct).ConfigureAwait(false);
    }

    public static async Task WritePdfAsync(MarkdownDocuments documents, ILocalHtmlPdfPrinter printer, IReadOnlyList<FileWorkItem> items, IReadOnlyList<string> texts,
        bool divider, bool pageBreaks, HtmlPrintOptions print, string name, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct)
    {
        var built = await BuildAsync(documents, items, texts, ImageMode.Embed, MarkdownDocuments.PrintBudget(string.Concat(texts)), keepRemote: false, divider, progress, ct).ConfigureAwait(false);
        string content = (pageBreaks ? "<style>.merged-part + .merged-part { break-before: page; }</style>" : string.Empty) + MarkdownDocuments.PrintDocument(built);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var document = await printer.PrepareAsync(source, ct).ConfigureAwait(false);
        await output.WriteFileFromPathAsync(items[0].Directory, name, ".pdf", (path, token) => printer.RenderAsync(document, path, print, token), ct).ConfigureAwait(false);
    }

    public static Task WriteTextAsync(FileOutputWriter output, string directory, string name, string extension, string content, CancellationToken ct) =>
        output.WriteFileAsync(directory, name, extension, async (stream, token) =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(content.AsMemory(), token).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// Runs a combined conversion inside a per-file action: every input is checked first, and a merge with a
    /// missing file is not written, because it would look complete but is not what the user previewed.
    /// </summary>
    public static async Task RunCombinedAsync(IReadOnlyList<SelectedFile> inputs,
        Func<SelectedFile, int, int, CancellationToken, Task<(FileWorkItem? Item, ActionFailure? Failure)>> prepare,
        Func<IReadOnlyList<FileWorkItem>, IProgress<double>?, Task> write, Func<Exception, string> describe,
        List<ActionFailure> failures, IProgress<ActionProgress>? progress, CancellationToken ct)
    {
        var items = new List<FileWorkItem>();
        for (int i = 0; i < inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (item, failure) = await prepare(inputs[i], i, inputs.Count, ct).ConfigureAwait(false);
            if (item is null) failures.Add(failure!); else items.Add(item);
        }
        if (failures.Count > 0) return;
        var step = progress is null ? null : new Progress<double>(p => progress.Report(new ActionProgress(Math.Clamp(p, 0, 1), null, SR.Get("Converting"))));
        try { await MapErrors(() => write(items, step)).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { failures.Add(new ActionFailure(null, describe(ex))); }
    }

    private static async Task<BuiltDocument> BuildAsync(MarkdownDocuments documents, IReadOnlyList<FileWorkItem> items, IReadOnlyList<string> texts,
        ImageMode mode, long budget, bool keepRemote, bool divider, IProgress<double>? progress, CancellationToken ct)
    {
        var body = new StringBuilder();
        string? title = null;
        int embedded = 0, remote = 0, unavailable = 0, unsupported = 0, removed = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var part = await documents.BuildAsync(texts[i], items[i].Path, mode, budget, ct, keepRemote, i == 0 ? null : $"part-{i + 1}-").ConfigureAwait(false);
            budget = Math.Max(0, budget - part.EmbeddedLength);
            title ??= part.Title;
            (embedded, remote, unavailable, unsupported, removed) = (embedded + part.Images.Embedded, remote + part.Images.Remote,
                unavailable + part.Images.Unavailable, unsupported + part.Images.Unsupported, removed + part.RemovedContent);
            if (i > 0 && divider) body.Append("<hr>");
            body.Append("<section class=\"merged-part\">").Append(part.Body).Append("</section>\n");
            progress?.Report(0.6 * (i + 1) / items.Count);
        }
        return new(body.ToString(), title!, new(embedded, remote, unavailable, unsupported), removed);
    }
}
