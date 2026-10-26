using PowerTrove.Pack.Pdf.Windows;
using PowerTrove.Abstractions.Ocr;
using PowerTrove.Abstractions.Office;
using PowerTrove.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(PowerTrove.Pack.Pdf.PdfPackFactory))]

namespace PowerTrove.Pack.Pdf;

public sealed class PdfPackFactory : IPackFactory
{
    private readonly ITextRecognizer? _textRecognizer;
    public PdfPackFactory() { }
    public PdfPackFactory(ITextRecognizer textRecognizer) => _textRecognizer = textRecognizer ?? throw new ArgumentNullException(nameof(textRecognizer));
    public ActionPackInfo Info => PdfPack.Describe();
    public IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new PdfPack(host.Files, host.GetService<IPdfPageRenderer>() ?? (OperatingSystem.IsWindows() ? new WindowsPdfRenderer() : null), host.GetService<IImageToJpegConverter>() ?? (OperatingSystem.IsWindows() ? new WicImageToJpegConverter() : null),
            host.GetService<IPdfImageRecompressor>() ?? (OperatingSystem.IsWindows() ? new WicPdfImageRecompressor() : null), _textRecognizer ?? host.GetService<ITextRecognizer>(), host.GetService<IOfficeConverter>());
    }
}
