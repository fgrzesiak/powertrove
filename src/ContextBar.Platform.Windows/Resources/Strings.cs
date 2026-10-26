using System.Globalization;
using System.Resources;

namespace ContextBar.Platform.Windows.Resources;

public static class Strings
{
    public static string OcrUnavailable => Get(nameof(OcrUnavailable));
    public static string OcrFailed => Get(nameof(OcrFailed));
    private static readonly ResourceManager ResourceManager = new("ContextBar.Platform.Windows.Resources.Strings", typeof(Strings).Assembly);

    private static string Get(string name) => ResourceManager.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    public static string ShellRevealFailed => Get(nameof(ShellRevealFailed));
    public static string ExcelNotAvailable => Get(nameof(ExcelNotAvailable));
    public static string ExcelWindowChanged => Get(nameof(ExcelWindowChanged));
    public static string ExcelWindowUnavailable => Get(nameof(ExcelWindowUnavailable));
    public static string ExcelItemNotRange => Get(nameof(ExcelItemNotRange));
    public static string ExcelDisjointSelection => Get(nameof(ExcelDisjointSelection));
    public static string ExcelRangeTooLarge => Get(nameof(ExcelRangeTooLarge));
    public static string ExcelInconsistentDimensions => Get(nameof(ExcelInconsistentDimensions));
    public static string ExcelInvalidRangeValues => Get(nameof(ExcelInvalidRangeValues));
    public static string ExcelAccessDenied => Get(nameof(ExcelAccessDenied));
    public static string ExcelError => Get(nameof(ExcelError));
    public static string ExcelCaptureBusy => Get(nameof(ExcelCaptureBusy));
    public static string ExcelCaptureNoData => Get(nameof(ExcelCaptureNoData));
    public static string ExcelCaptureStartFailed => Get(nameof(ExcelCaptureStartFailed));
    public static string ExcelCaptureTimedOut => Get(nameof(ExcelCaptureTimedOut));

    public static string ExplorerCaptureBusy => Get(nameof(ExplorerCaptureBusy));
    public static string ExplorerNoSelection => Get(nameof(ExplorerNoSelection));
    public static string ExplorerUnsupportedView => Get(nameof(ExplorerUnsupportedView));
    public static string ExplorerError => Get(nameof(ExplorerError));
    public static string ExplorerCaptureStartFailed => Get(nameof(ExplorerCaptureStartFailed));
    public static string ExplorerCaptureTimedOut => Get(nameof(ExplorerCaptureTimedOut));

    public static string ImagingCannotPreserveMetadata => Get(nameof(ImagingCannotPreserveMetadata));
    public static string ImageDecodeFailed => Get(nameof(ImageDecodeFailed));
    public static string ImageEncodeFailed => Get(nameof(ImageEncodeFailed));
    public static string ImageUnsupportedFormat => Get(nameof(ImageUnsupportedFormat));

    public static string MediaOpenFailed => Get(nameof(MediaOpenFailed));
    public static string MediaCodecNotFound => Get(nameof(MediaCodecNotFound));
    public static string MediaInvalidProfile => Get(nameof(MediaInvalidProfile));
    public static string MediaConversionNotSupported => Get(nameof(MediaConversionNotSupported));
    public static string MediaConversionFailed => Get(nameof(MediaConversionFailed));
    public static string MediaUnsupportedAudio => Get(nameof(MediaUnsupportedAudio));
    public static string MediaNoVideoTrack => Get(nameof(MediaNoVideoTrack));
    public static string MediaUnsupportedOperation => Get(nameof(MediaUnsupportedOperation));

    public static string PdfPasswordProtected => Get(nameof(PdfPasswordProtected));
    public static string PdfOpenFailed => Get(nameof(PdfOpenFailed));
}
