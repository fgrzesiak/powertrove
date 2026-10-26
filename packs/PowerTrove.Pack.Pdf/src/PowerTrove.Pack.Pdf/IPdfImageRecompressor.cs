using System.Threading;
using System.Threading.Tasks;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf;

/// <summary>
/// Port for recompressing images in PDF files. Implemented with the Windows Imaging Component
/// in Platform.Windows to keep the PDF pack independent.
/// </summary>
public interface IPdfImageRecompressor
{
    Task<PdfImageReplacement?> RecompressAsync(byte[] encoded, string? filter, PdfImageInfo info, int maxWidth, int maxHeight, int quality, CancellationToken cancellationToken);
}
