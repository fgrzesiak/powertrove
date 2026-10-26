using System;

namespace FileKit.Pdf;

internal static class PdfPlacement
{
    public static (double a, double b, double c, double d, double e, double f) GetMatrix(
        double boxX, double boxY, double boxW, double boxH,
        int pageRotate, PdfAnchor anchor,
        double objW, double objH, double margin, double objRotateDegrees = 0, double? offsetX = null, double? offsetY = null)
    {
        double visW = (pageRotate == 90 || pageRotate == 270) ? boxH : boxW;
        double visH = (pageRotate == 90 || pageRotate == 270) ? boxW : boxH;

        double vx = 0;
        double vy = 0;

        switch (anchor)
        {
            case PdfAnchor.TopLeft:
                vx = margin;
                vy = visH - margin - objH;
                break;
            case PdfAnchor.TopCenter:
                vx = (visW - objW) / 2.0;
                vy = visH - margin - objH;
                break;
            case PdfAnchor.TopRight:
                vx = visW - margin - objW;
                vy = visH - margin - objH;
                break;
            case PdfAnchor.MiddleLeft:
                vx = margin;
                vy = (visH - objH) / 2.0;
                break;
            case PdfAnchor.Center:
                vx = (visW - objW) / 2.0;
                vy = (visH - objH) / 2.0;
                break;
            case PdfAnchor.MiddleRight:
                vx = visW - margin - objW;
                vy = (visH - objH) / 2.0;
                break;
            case PdfAnchor.BottomLeft:
                vx = margin;
                vy = margin;
                break;
            case PdfAnchor.BottomCenter:
                vx = (visW - objW) / 2.0;
                vy = margin;
                break;
            case PdfAnchor.BottomRight:
                vx = visW - margin - objW;
                vy = margin;
                break;
        }

        if (offsetX.HasValue) vx = offsetX.Value;
        if (offsetY.HasValue) vy = offsetY.Value;

        double vcx = vx + objW / 2.0;
        double vcy = vy + objH / 2.0;

        double pcx = vcx;
        double pcy = vcy;

        if (pageRotate == 90)
        {
            pcx = visH - vcy; // == boxW - vcy
            pcy = vcx;
        }
        else if (pageRotate == 180)
        {
            pcx = boxW - vcx;
            pcy = boxH - vcy;
        }
        else if (pageRotate == 270)
        {
            pcx = vcy;
            pcy = visW - vcx; // == boxH - vcx
        }

        pcx += boxX;
        pcy += boxY;

        double rad = (pageRotate + objRotateDegrees) * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);

        double e = pcx - (objW / 2.0) * cos + (objH / 2.0) * sin;
        double f = pcy - (objW / 2.0) * sin - (objH / 2.0) * cos;

        return (
            Math.Abs(cos) < 1e-10 ? 0 : cos,
            Math.Abs(sin) < 1e-10 ? 0 : sin,
            Math.Abs(-sin) < 1e-10 ? 0 : -sin,
            Math.Abs(cos) < 1e-10 ? 0 : cos,
            e, f);
    }
}
