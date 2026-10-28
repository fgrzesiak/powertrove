using System.Globalization;
using PowerTrove.Abstractions.Office;

namespace PowerTrove.Pack.Office;

public sealed class OfficePack : IActionPack
{
    public OfficePack(FileActionServices services, IOfficeConverter? converter = null)
    {
        Actions = new IAction[]
        {
            new OfficeConvertAction(OfficeApplication.Word, false, services, converter),
            new OfficeConvertAction(OfficeApplication.Excel, false, services, converter),
            new OfficeConvertAction(OfficeApplication.PowerPoint, false, services, converter),
            new OfficeConvertAction(OfficeApplication.Word, true, services, converter)
        };
    }
    public ActionPackInfo Info { get; } = Describe();

    internal static ActionPackInfo Describe() => new("powertrove.office", SR.Get("PackTitle"), PowerTrove.Packs.PackReleaseVersion.Read(typeof(OfficePack).Assembly), "PowerTrove");
    public IReadOnlyList<IAction> Actions { get; }
}

public sealed record OfficeOptions;

public sealed class OfficeConvertAction : PerFileAction<OfficeOptions>
{
    private readonly OfficeApplication _application;
    private readonly bool _pdfToWord;
    private readonly IOfficeConverter? _converter;
    private readonly FileInputFilter _filter;

    public OfficeConvertAction(OfficeApplication application, bool pdfToWord, FileActionServices services, IOfficeConverter? converter = null) : base(services)
    {
        if (pdfToWord && application != OfficeApplication.Word) throw new ArgumentException("PDF reflow requires Word.", nameof(application));
        _application = application; _pdfToWord = pdfToWord; _converter = converter;
        string kind = pdfToWord ? "PdfToWord" : application.ToString();
        string verb = pdfToWord ? "pdf-to-word" : application.ToString().ToLowerInvariant() + "-to-pdf";
        var extensions = application switch { OfficeApplication.Word => OfficeFormats.WordExtensions, OfficeApplication.Excel => OfficeFormats.ExcelExtensions, _ => OfficeFormats.PowerPointExtensions };
        _filter = new FileInputFilter(SR.Get("Inputs"), pdfToWord ? new[] { ".pdf" } : extensions,
            new[] { pdfToWord ? FileFormats.Pdf : OfficeFormats.FormatFor(application) });
        Descriptor = new ActionDescriptor("powertrove.office." + verb, SR.Get("Title" + kind), SR.Get("Description" + kind),
            new HashSet<ContentKind> { ContentKind.Files }, new[] { "office", "word", "excel", "powerpoint", "pdf", "convert", "export" },
            RequiresPreview: true, ChangesFiles: true,
            Selection: SelectionRequirements.Any(pdfToWord ? "pdf" : application == OfficeApplication.Excel ? "spreadsheet" : "document"));
    }
    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => _filter;
    protected override int MaximumInputs => 100;
    protected override string? UnavailableReason => _converter?.IsAvailable(_application) == true ? null : SR.Get("Missing" + _application);
    protected override OfficeOptions ReadOptions(ActionParameterValues values) => new();
    protected override string ProgressVerb => SR.Get("Progress");
    protected override string DescribeOperation(int count, OfficeOptions options) => string.Format(CultureInfo.CurrentCulture,
        SR.Get((_pdfToWord ? "Reflow" : "Export") + (count == 1 ? "_One" : "_Other")), count);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, OfficeOptions options)
    {
        yield return SR.Get(_pdfToWord ? "ReflowWarning" : "ExportWarning");
    }
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, OfficeOptions options)
    {
        yield return new(Path.GetFileNameWithoutExtension(input.Name), _pdfToWord ? ".docx" : ".pdf");
    }
    protected override async Task ProcessAsync(FileWorkItem item, OfficeOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        try
        {
            await output.WriteFileFromPathAsync(item.Directory, item.BaseName, _pdfToWord ? ".docx" : ".pdf",
                (destination, ct) => _pdfToWord
                    ? _converter!.ConvertPdfToWordAsync(item.Path, destination, ct)
                    : _converter!.ConvertToPdfAsync(item.Path, destination, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (OfficeConversionException ex) { throw new FileProcessingException(SR.Get("Error" + ex.Failure)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new FileProcessingException(SR.Get("ErrorConversionFailed")); }
    }
}
