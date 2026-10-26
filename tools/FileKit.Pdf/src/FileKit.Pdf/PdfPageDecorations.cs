namespace FileKit.Pdf;

public abstract record PdfDecoration;
public sealed record PdfTextDecoration(string Text, PdfAnchor Anchor, double FontSizePt, PdfColor Color, double Opacity = 1,
    double RotationDegrees = 0, double MarginPt = 28.35, PdfStandardFont Font = PdfStandardFont.Helvetica, double? OffsetXPt = null, double? OffsetYPt = null,
    bool Invisible = false, double? HorizontalScaling = null) : PdfDecoration;
public sealed record PdfImageDecoration(PdfImage Image, PdfAnchor Anchor, double WidthPt, double Opacity = 1, double MarginPt = 28.35) : PdfDecoration;
public enum PdfAnchor { TopLeft, TopCenter, TopRight, MiddleLeft, Center, MiddleRight, BottomLeft, BottomCenter, BottomRight }
public enum PdfStandardFont { Helvetica, HelveticaBold, TimesRoman, Courier }
public readonly record struct PdfColor(double R, double G, double B);
