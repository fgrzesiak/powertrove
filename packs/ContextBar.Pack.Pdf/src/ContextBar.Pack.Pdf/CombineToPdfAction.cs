using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions;
using ContextBar.Abstractions.Files;
using FileKit.Pdf;
using ContextBar.Abstractions.Office;

namespace ContextBar.Pack.Pdf;

public sealed record CombineToPdfOptions(PdfPageSize PageSize, string Order);

/// <summary>
/// "Combine into One PDF" for mixed selections (spec P26): PDFs contribute all their pages, images
/// become pages, everything in file-name order. JPEG and PNG are embedded directly; other image
/// formats go through <see cref="IImageToJpegConverter"/> when the host provides one.
/// </summary>
public sealed class CombineToPdfAction : CombineFilesAction<CombineToPdfOptions>
{
    public const string ActionId = "contextbar.pdf.combine";

    private readonly FileInputFilter _filter = new(
        SR.GetString("CombineToPdf_Inputs"),
        new[] { ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif" }
            .Concat(OfficeFormats.WordExtensions).Concat(OfficeFormats.ExcelExtensions).Concat(OfficeFormats.PowerPointExtensions),
        new[] { FileFormats.Pdf, FileFormats.Jpeg, FileFormats.Png, FileFormats.Gif, FileFormats.Bmp, FileFormats.Tiff,
                FileFormats.WebP, FileFormats.Heic, FileFormats.Avif, OfficeFormats.Word, OfficeFormats.Excel, OfficeFormats.PowerPoint });

    private readonly IImageToJpegConverter? _converter;
    private readonly IOfficeConverter? _office;

    public CombineToPdfAction(FileActionServices services, IImageToJpegConverter? converter = null, IOfficeConverter? officeConverter = null) : base(services)
    {
        _converter = converter;
        _office = officeConverter;
        Descriptor = new ActionDescriptor(
            ActionId, SR.GetString("CombineToPdf_Title"),
            SR.GetString("CombineToPdf_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "combine", "merge", "images", "photos", "join", "scan" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("PageSize", SR.GetString("ImagesToPdfAction_ParamTitle_PageSize"), "a4", new[]
                {
                    new ActionParameterChoice("a4", SR.GetString("ImagesToPdfAction_ParamChoice_A4")),
                    new ActionParameterChoice("letter", SR.GetString("ImagesToPdfAction_ParamChoice_Letter")),
                    new ActionParameterChoice("fit", SR.GetString("ImagesToPdfAction_ParamChoice_Fit"))
                }),
                ActionParameterDefinition.Choice("Order", SR.GetString("CombineToPdf_ParamTitle_Order"), "name", new[]
                {
                    new ActionParameterChoice("name", SR.GetString("CombineToPdf_ParamChoice_Name")),
                    new ActionParameterChoice("pdfs-first", SR.GetString("CombineToPdf_ParamChoice_PdfsFirst")),
                    new ActionParameterChoice("images-first", SR.GetString("CombineToPdf_ParamChoice_ImagesFirst"))
                })
            },
            Selection: new SelectionRequirements(
                new HashSet<string>(new[] { "pdf", "image", "document", "spreadsheet" }, System.StringComparer.OrdinalIgnoreCase),
                MinItems: 2, AllowOtherItems: false, IdealMinItems: 2,
                FamilyLimits: new Dictionary<string, FamilyLimit>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pdf"] = new(0, 500), ["image"] = new(0, 500),
                    ["document"] = new(0, 500), ["spreadsheet"] = new(0, 500)
                },
                IdealMinDistinctFamilies: 2));
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => _filter;
    protected override int MaximumInputs => 500;
    protected override bool RequiresEntireSelection => true;

    protected override string? ValidateSelection(IReadOnlyList<SelectedFile> inputs) =>
        inputs.Select(i => OfficeFormats.ApplicationFor(i.Name)).Any(kind => kind.HasValue && _office?.IsAvailable(kind.Value) != true)
            ? SR.GetString("CombineToPdf_OfficeMissing") : null;

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        var preview = await base.PreviewAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (context.Content is FileSelection selection && ValidateSelection(selection.Files) is string missing)
            return preview with { IsBlocked = true, ExpectedOutputs = System.Array.Empty<string>(), Warnings = preview.Warnings.Concat(new[] { missing }).ToArray() };
        return preview;
    }

    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, CombineToPdfOptions options)
    {
        if (inputs.Any(i => OfficeFormats.ApplicationFor(i.Name).HasValue)) yield return SR.GetString("CombineToPdf_OfficeLayoutWarning");
    }

    protected override CombineToPdfOptions ReadOptions(ActionParameterValues values) => new(
        values.GetChoice("PageSize") switch { "letter" => PdfPageSize.Letter, "fit" => PdfPageSize.FitImage, _ => PdfPageSize.A4 },
        values.GetChoice("Order")
    );

    protected override string DescribeOperation(int inputCount, CombineToPdfOptions options) =>
        string.Format(CultureInfo.CurrentCulture, SR.GetString("CombineToPdf_Operation"), inputCount);

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, CombineToPdfOptions options) =>
        new(Path.GetFileNameWithoutExtension(inputs[0].Name) + " (combined)", ".pdf");

    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, CombineToPdfOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var orderedItems = options.Order switch
        {
            "pdfs-first" => items.OrderBy(i => i.DetectedFormat == FileFormats.Pdf ? 0 : 1).ThenBy(i => i.File.Name, NaturalFileNameComparer.Instance).ToList(),
            "images-first" => items.OrderBy(i => SelectionFamilies.Of(i.File) == "image" ? 0 : 1).ThenBy(i => i.File.Name, NaturalFileNameComparer.Instance).ToList(),
            _ => items.OrderBy(i => i.File.Name, NaturalFileNameComparer.Instance).ToList()
        };

        // Load everything first, so a bad item fails before any output is written.
        var parts = new List<object>(orderedItems.Count);
        for (int i = 0; i < orderedItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            parts.Add(await LoadPartAsync(orderedItems[i], cancellationToken).ConfigureAwait(false));
            progress?.Report(0.7 * (i + 1) / orderedItems.Count);
        }

        var layout = new PdfImagePageLayout(options.PageSize);
        var planned = PlanOutput(items.Select(i => i.File).ToList(), options);
        await output.WriteFileAsync(items[0].Directory, planned.BaseName, planned.Extension,
            (stream, ct) => PdfSupport.RunAsync(() =>
            {
                var builder = new PdfDocumentBuilder();
                foreach (var part in parts)
                {
                    ct.ThrowIfCancellationRequested();
                    if (part is PdfDocument document)
                        builder.AddPages(document, Enumerable.Range(0, document.PageCount));
                    else
                        builder.AddImagePage((PdfImage)part, layout);
                }
                builder.Save(stream, cancellationToken: ct);
            }, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> LoadPartAsync(FileWorkItem item, CancellationToken cancellationToken)
    {
        string format = item.DetectedFormat ?? string.Empty;
        if (OfficeFormats.ApplicationFor(item.File.Name) is OfficeApplication application)
        {
            if (_office?.IsAvailable(application) != true) throw new FileProcessingException(SR.GetString("CombineToPdf_OfficeMissing"));
            string folder = Path.Combine(Path.GetTempPath(), "ContextBar.OfficePdf", System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string pdf = Path.Combine(folder, "converted.pdf");
                try { await _office.ConvertToPdfAsync(item.Path, pdf, cancellationToken).ConfigureAwait(false); }
                catch (OfficeConversionException ex) { throw new FileProcessingException(SR.GetString("CombineToPdf_Office" + ex.Failure)); }
                catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (System.Exception) { throw new FileProcessingException(SR.GetString("CombineToPdf_OfficeConversionFailed")); }
                return await PdfSupport.LoadAsync(pdf, cancellationToken).ConfigureAwait(false);
            }
            finally { Directory.Delete(folder, true); }
        }
        if (format == FileFormats.Pdf)
            return await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);

        if (format is FileFormats.Jpeg or FileFormats.Png)
        {
            return await PdfSupport.LoadImageAsync(() => PdfImage.Load(item.Path), cancellationToken).ConfigureAwait(false);
        }

        if (_converter is null || !_converter.CanConvert(format))
            throw new FileProcessingException(SR.GetString("CombineToPdf_Error_UnsupportedImage"));

        using var jpeg = new MemoryStream();
        await _converter.ConvertAsync(item.Path, format, jpeg, cancellationToken).ConfigureAwait(false);
        byte[] bytes = jpeg.ToArray();
        return await PdfSupport.LoadImageAsync(() => PdfImage.Load(bytes), cancellationToken).ConfigureAwait(false);
    }
}
