namespace PowerTrove.Pack.Pdf;

/// <summary>PDF page tools built on the self-contained FileKit.Pdf library.</summary>
public sealed class PdfPack : IActionPack
{
    public PdfPack(FileActionServices services, IPdfPageRenderer? renderer = null, IImageToJpegConverter? imageConverter = null, IPdfImageRecompressor? recompressor = null, PowerTrove.Abstractions.Ocr.ITextRecognizer? textRecognizer = null, PowerTrove.Abstractions.Office.IOfficeConverter? officeConverter = null, FileKit.Html.ILocalHtmlPdfPrinter? htmlRenderer = null, FileKit.Html.IEpubDocumentBuilder? epubBuilder = null, FileKit.Html.IEmailPrintReader? emailReader = null, FileKit.Tabular.IPositionedDocumentReader? positionedReader = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        Actions = new IAction[]
        {
            new MergePdfAction(services),
            new CombineToPdfAction(services, imageConverter, officeConverter),
            new InterleavePdfAction(services, renderer),
            new InsertPdfAction(services),
            new InsertBlankPagesAction(services),
            new SplitPdfAction(services),
            new ExtractPdfPagesAction(services),
            new RemovePdfPagesAction(services),
            new RemoveBlankPagesPdfAction(services, renderer),
            new OrganizePagesPdfAction(services),
            new ReorderPdfPagesAction(services),
            new RotatePdfAction(services),
            new RemovePdfMetadataAction(services),
            new EditPdfMetadataAction(services),
            new HeaderFooterPdfAction(services),
            new AddPageNumbersPdfAction(services),
            new ImagesToPdfAction(services),
            new PdfToImagesAction(services, renderer),
            new ExtractPdfImagesAction(services),
            new PdfToTextAction(services),
            new StampPdfImageAction(services),
            new StampSelectedImagePdfAction(services),
            new PdfInfoAction(services.TypeVerifier),
            new WatermarkPdfAction(services),
            new UnlockPdfAction(services),
            new ProtectPdfAction(services),
            new FlattenPdfAction(services),
            new NUpPdfAction(services),
            new BookletPdfAction(services),
            new CompressPdfAction(services, recompressor),
            new OcrPdfAction(services, renderer, textRecognizer),
            new RepairPdfAction(services), new HtmlToPdfAction(services, htmlRenderer), new EpubToPdfAction(services, htmlRenderer, epubBuilder), new EmlToPdfAction(services, htmlRenderer, emailReader), new PdfToExcelAction(services, positionedReader)
        };
    }

    public ActionPackInfo Info { get; } = Describe();

    internal static ActionPackInfo Describe() => new("powertrove.pdf", SR.GetString("PdfPack_Title"), PowerTrove.Packs.PackReleaseVersion.Read(typeof(PdfPack).Assembly), "PowerTrove");

    public IReadOnlyList<IAction> Actions { get; }
}
