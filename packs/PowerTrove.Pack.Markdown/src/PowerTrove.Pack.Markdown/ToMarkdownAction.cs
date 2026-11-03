using System.Globalization;
using System.Text;
using FileKit.Text;
using PowerTrove.Pack.Markdown.Conversion;

namespace PowerTrove.Pack.Markdown;

public sealed record ToMarkdownOptions(bool Images, bool Notes, bool Outputs);

/// <summary>
/// Word, PowerPoint, Excel, PDF, EPUB, Jupyter and HTML files to Markdown, each read for its structure:
/// headings, lists, emphasis, links, tables, code and pictures. Pictures are saved in a
/// "name-images" folder next to the new Markdown file. Nothing runs, nothing is downloaded, and the source stays unchanged.
/// </summary>
public sealed class ToMarkdownAction(FileActionServices services, IMarkdownEngine? engine, IPdfTextReader? pdfReader) : PerFileAction<ToMarkdownOptions>(services)
{
    public const string Id = "powertrove.markdown.to-markdown";
    private static readonly string[] Formats = [".docx", ".docm", ".pptx", ".pptm", ".xlsx", ".xlsm", ".pdf", ".epub", ".ipynb", ".html", ".htm", ".xhtml"];

    public override ActionDescriptor Descriptor { get; } = new(Id, SR.Get("ToMarkdownTitle"), SR.Get("ToMarkdownDescription"),
        new HashSet<ContentKind> { ContentKind.Files },
        ["markdown", "md", "convert", "word", "docx", "powerpoint", "pptx", "excel", "xlsx", "pdf", "epub", "ebook", "jupyter", "ipynb", "notebook", "html", "web", "page", "text"],
        true, true,
        Parameters:
        [
            ActionParameterDefinition.Boolean("Images", SR.Get("ToMarkdownImages"), true, SR.Get("ToMarkdownImagesHelp")),
            ActionParameterDefinition.Boolean("Notes", SR.Get("ToMarkdownNotesOption"), true),
            ActionParameterDefinition.Boolean("Outputs", SR.Get("ToMarkdownOutputsOption"), true)
        ],
        Selection: SelectionRequirements.Any("document", "spreadsheet", "pdf", "other"));

    protected override FileInputFilter Input => new(SR.Get("ToMarkdownFiles"), Formats, null);
    protected override int MaximumInputs => 100;
    protected override string? UnavailableReason => engine is null ? SR.Get("EngineUnavailable") : null;
    protected override ToMarkdownOptions ReadOptions(ActionParameterValues values) => new(values.GetBoolean("Images"), values.GetBoolean("Notes"), values.GetBoolean("Outputs"));
    protected override string ProgressVerb => SR.Get("Converting");
    protected override string DescribeOperation(int count, ToMarkdownOptions options) =>
        SR.Count("ToMarkdownOperation", count);

    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, ToMarkdownOptions options)
    {
        yield return SR.Get("ToMarkdownNotice");
        if (options.Images) yield return SR.Get("ToMarkdownImagesNotice");
        if (inputs.Any(i => i.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))) yield return SR.Get("ToMarkdownPdfNotice");
    }

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, ToMarkdownOptions options)
    {
        yield return new(Path.GetFileNameWithoutExtension(input.Name), ".md");
    }

    protected override async Task ProcessAsync(FileWorkItem item, ToMarkdownOptions options, FileOutputWriter output, CancellationToken ct)
    {
        ConvertedMarkdown converted;
        try { converted = await ConvertAsync(item, options, ct).ConfigureAwait(false); }
        catch (DocumentFormatException ex) { throw new FileProcessingException(ex.Message); }
        catch (MarkdownLimitException) { throw new FileProcessingException(SR.Get("TooComplex")); }
        catch (PdfTextException ex) { throw new FileProcessingException(SR.Get(ex.Failure switch { PdfTextFailure.Encrypted => "ToMarkdownPdfEncrypted", PdfTextFailure.TooComplex => "TooComplex", _ => "ToMarkdownInvalid" })); }
        catch (InvalidDataException) { throw new FileProcessingException(SR.Get("ToMarkdownInvalid")); }

        // Pictures first, as one new folder next to the Markdown file; its final name (never an existing folder)
        // goes into the links.
        var paths = new string?[converted.Images.Count];
        if (options.Images && converted.Images.Count > 0)
        {
            string folder = await output.WriteDirectoryAsync(item.Directory, Stem(item.BaseName) + "-images", async (temporary, token) =>
            {
                for (int i = 0; i < converted.Images.Count; i++)
                {
                    await using var stream = Services.FileSystem.CreateFile(Path.Combine(temporary, (i + 1).ToString(CultureInfo.InvariantCulture) + converted.Images[i].Extension));
                    await stream.WriteAsync(converted.Images[i].Bytes, token).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
            string name = Encode(Path.GetFileName(folder));
            for (int i = 0; i < paths.Length; i++) paths[i] = name + "/" + (i + 1).ToString(CultureInfo.InvariantCulture) + converted.Images[i].Extension;
        }
        string markdown = converted.Resolve(i => paths[i]);
        if (markdown.Trim().Length == 0) throw new FileProcessingException(SR.Get("NoContent"));
        await MarkdownMerge.WriteTextAsync(output, item.Directory, item.BaseName, ".md", markdown, ct).ConfigureAwait(false);
    }

    private async Task<ConvertedMarkdown> ConvertAsync(FileWorkItem item, ToMarkdownOptions options, CancellationToken ct)
    {
        string extension = Path.GetExtension(item.Path).ToLowerInvariant();
        await using var input = Services.FileSystem.OpenRead(item.Path);
        switch (extension)
        {
            case ".docx" or ".docm": return await DocxConverter.ConvertAsync(input, ct).ConfigureAwait(false);
            case ".pptx" or ".pptm": return await PptxConverter.ConvertAsync(input, item.BaseName, options.Notes, ct).ConfigureAwait(false);
            case ".xlsx" or ".xlsm": return await TableConverter.ConvertXlsxAsync(input, ct).ConfigureAwait(false);
            case ".epub": return await EpubConverter.ConvertAsync(input, engine!, ct).ConfigureAwait(false);
            case ".ipynb": return await NotebookConverter.ConvertAsync(input, options.Outputs, ct).ConfigureAwait(false);
            case ".pdf":
                if (pdfReader is null) throw new FileProcessingException(SR.Get("EngineUnavailable"));
                var document = await pdfReader.ReadAsync(input, options.Images, ct).ConfigureAwait(false);
                return await Task.Run(() => PdfLayoutConverter.Convert(document, ct), ct).ConfigureAwait(false);
            default:
                string html;
                try { html = (await TextDecoding.ReadAsync(input, MarkdownDocuments.MaximumHtmlInputBytes, ct).ConfigureAwait(false)).Text; }
                catch (TextLimitException) { throw new FileProcessingException(SR.Get("TooLarge")); }
                catch (DecoderFallbackException) { throw new FileProcessingException(SR.Get("InvalidText")); }
                var conversion = await Task.Run(() => engine!.ConvertHtml(html, ct), ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(conversion.Markdown)) throw new DocumentFormatException(SR.Get("NoContent"));
                return new(conversion.Markdown, [], conversion.OmittedImages, conversion.RemovedContent);
        }
    }

    /// <summary>A file name that stays a valid, portable Markdown reference.</summary>
    private static string Stem(string name)
    {
        var builder = new StringBuilder();
        foreach (char c in name.Normalize(NormalizationForm.FormC))
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : c == ' ' ? '-' : '_');
        string result = builder.ToString().Trim('.', '-', '_');
        return result.Length == 0 ? "image" : result.Length > 60 ? result[..60] : result;
    }

    private static string Encode(string relative)
    {
        var builder = new StringBuilder();
        foreach (char c in relative.Replace('\\', '/'))
            builder.Append(c is ' ' or '%' or '(' or ')' or '<' or '>' or '"' ? Uri.EscapeDataString(c.ToString()) : c.ToString());
        return builder.ToString();
    }
}
