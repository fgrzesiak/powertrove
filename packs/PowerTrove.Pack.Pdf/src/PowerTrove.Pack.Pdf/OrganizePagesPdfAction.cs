using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf;

public sealed record OrganizePagesOptions(string Pages);

/// <summary>
/// Builds one new PDF from pages of one or more selected PDFs in any order, with per-page rotation
/// and with pages left out ("Organize Pages"). The desktop host collects the page list visually
/// (page organizer); the action itself is headless and takes it as the "Pages" parameter in
/// <see cref="PageArrangement"/> text form. Sources keep their processing order
/// (<see cref="SourceOrder"/>), which the organizer uses for file numbers.
/// </summary>
public sealed class OrganizePagesPdfAction : CombineFilesAction<OrganizePagesOptions>
{
    public const string ActionId = "contextbar.pdf.organize";

    public OrganizePagesPdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            ActionId, SR.GetString("OrganizePages_Title"),
            SR.GetString("OrganizePages_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "organize", "pages", "reorder", "rotate", "delete", "arrange", "thumbnails" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Text("Pages", SR.GetString("OrganizePages_Param_Pages"), "",
                    SR.GetString("OrganizePages_Param_PagesDescription"), "1:1, 1:3@90, 2:1")
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 5 });
    }

    /// <summary>The order in which selected PDFs become files 1, 2, … of an arrangement.</summary>
    public static IReadOnlyList<SelectedFile> SourceOrder(IEnumerable<SelectedFile> pdfs) => NaturalFileNameComparer.Sort(pdfs);

    public override ActionDescriptor Descriptor { get; }
    protected override int MinimumInputs => 1;
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override OrganizePagesOptions ReadOptions(ActionParameterValues values) => new(values.GetText("Pages"));

    protected override string? ValidateOptions(OrganizePagesOptions options) =>
        PageArrangement.TryParse(options.Pages, out _, out var error) ? null : error;

    protected override string DescribeOperation(int inputCount, OrganizePagesOptions options)
    {
        int pages = PageArrangement.TryParse(options.Pages, out var list, out _) ? list.Count : 0;
        return string.Format(CultureInfo.CurrentCulture, SR.GetString("OrganizePages_Operation"), pages);
    }

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, OrganizePagesOptions options) =>
        new(Path.GetFileNameWithoutExtension(inputs[0].Name) + " (organized)", ".pdf");

    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, OrganizePagesOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!PageArrangement.TryParse(options.Pages, out var pages, out var syntaxError))
            throw new FileProcessingException(syntaxError!);

        // Load only the sources the arrangement uses; file numbers stay those of the full selection.
        var documents = new PdfDocument?[items.Count];
        var used = pages.Select(p => p.Source).Where(s => s < items.Count).Distinct().ToList();
        for (int i = 0; i < used.Count; i++)
        {
            documents[used[i]] = await PdfSupport.LoadAsync(items[used[i]].Path, cancellationToken).ConfigureAwait(false);
            progress?.Report(0.6 * (i + 1) / used.Count);
        }

        var counts = documents.Select(d => d?.PageCount ?? 0).ToList();
        if (PageArrangement.Validate(pages, counts) is { } error)
            throw new FileProcessingException(error);

        var planned = PlanOutput(items.Select(i => i.File).ToList(), options);
        await output.WriteFileAsync(items[0].Directory, planned.BaseName, planned.Extension,
            (stream, ct) => PdfSupport.RunAsync(() =>
            {
                var builder = new PdfDocumentBuilder();
                foreach (var page in pages)
                {
                    ct.ThrowIfCancellationRequested();
                    builder.AddPage(documents[page.Source]!, page.Page, page.Rotation);
                }
                builder.Save(stream, cancellationToken: ct);
            }, ct),
            cancellationToken).ConfigureAwait(false);
    }
}
