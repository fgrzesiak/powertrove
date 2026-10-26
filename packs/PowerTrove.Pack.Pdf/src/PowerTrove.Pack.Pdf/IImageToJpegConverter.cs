using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Pack.Pdf;

/// <summary>
/// Port for turning image formats that FileKit.Pdf cannot embed (WebP, HEIC, BMP, TIFF, GIF, …)
/// into JPEG pages. Implemented with the Windows Imaging Component in Platform.Windows, so the
/// PDF pack stays independent of the image pack and its engines (ADR 0007).
/// </summary>
public interface IImageToJpegConverter
{
    /// <summary>True when <paramref name="format"/> (a FileFormats id) can be converted.</summary>
    bool CanConvert(string format);

    /// <summary>Writes the image at <paramref name="path"/> as an upright JPEG into <paramref name="jpeg"/>.</summary>
    Task ConvertAsync(string path, string format, Stream jpeg, CancellationToken cancellationToken);
}
