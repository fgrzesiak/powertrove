using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FileKit.Pdf;

public static partial class PdfTools
{
    public static void CreateNUp(PdfDocument document, Stream output, int perSheet, PdfPageSize sheetSize, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);

        if (perSheet != 2 && perSheet != 4)
            throw new ArgumentException("perSheet must be 2 or 4.", nameof(perSheet));

        double sheetWidth = sheetSize == PdfPageSize.Letter ? 612 : 595.28;
        double sheetHeight = sheetSize == PdfPageSize.Letter ? 792 : 841.89;

        // Auto-orient: if source pages are mostly landscape, we might want to rotate the sheet or something,
        // but for N-up, typically 2-up is 2 portrait pages side-by-side on a landscape sheet, or 2 landscape pages on a portrait sheet.
        // Let's implement standard reading order placement.

        // 5 mm gutter = 5 / 25.4 * 72 = 14.173 points
        double gutter = 14.1732;

        var builder = new PdfDocumentBuilder();

        // 2-up:
        // Place two pages side-by-side on a landscape sheet.
        // Wait, "orientation auto" means we decide based on the n-up.
        // 2-up standard: Sheet is landscape. 2 pages side by side.
        // 4-up standard: Sheet is portrait. 2x2 grid.

        bool isLandscapeSheet = (perSheet == 2);
        double actualSheetWidth = isLandscapeSheet ? sheetHeight : sheetWidth;
        double actualSheetHeight = isLandscapeSheet ? sheetWidth : sheetHeight;

        int cols = 2;
        int rows = perSheet == 2 ? 1 : 2;

        double cellWidth = (actualSheetWidth - gutter * (cols + 1)) / cols;
        double cellHeight = (actualSheetHeight - gutter * (rows + 1)) / rows;

        for (int i = 0; i < document.PageCount; i += perSheet)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var layers = new List<(PdfDocument, int, (double, double, double, double, double, double))>();

            for (int j = 0; j < perSheet && i + j < document.PageCount; j++)
            {
                int pageIndex = i + j;
                var page = document.Pages[pageIndex];

                int col = j % cols;
                int row = rows - 1 - (j / cols); // Reading order: top to bottom

                double x = gutter + col * (cellWidth + gutter);
                double y = gutter + row * (cellHeight + gutter);

                var (w, h) = GetPageDimensions(document, page);

                double scaleX = cellWidth / w;
                double scaleY = cellHeight / h;
                double scale = Math.Min(scaleX, scaleY);

                double dx = x + (cellWidth - w * scale) / 2;
                double dy = y + (cellHeight - h * scale) / 2;

                layers.Add((document, pageIndex, (scale, 0, 0, scale, dx, dy)));
            }

            builder.AddComposedPage(actualSheetWidth, actualSheetHeight, layers);
        }

        builder.Save(output, options, cancellationToken);
    }

    public static void CreateBooklet(PdfDocument document, Stream output, PdfPageSize sheetSize, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);

        // Saddle-stitch imposition.
        // Pad to a multiple of 4.
        int paddedCount = (document.PageCount + 3) / 4 * 4;

        double sheetWidth = sheetSize == PdfPageSize.Letter ? 612 : 595.28;
        double sheetHeight = sheetSize == PdfPageSize.Letter ? 792 : 841.89;

        // 2-up on landscape sheets.
        double actualSheetWidth = sheetHeight;
        double actualSheetHeight = sheetWidth;

        double gutter = 14.1732;
        double cellWidth = (actualSheetWidth - gutter * 3) / 2;
        double cellHeight = actualSheetHeight - gutter * 2;

        var builder = new PdfDocumentBuilder();

        int half = paddedCount / 2;

        for (int i = 0; i < half; i += 2)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Sheet front (e.g., pages 8 and 1 for an 8-page booklet)
            int leftPage = paddedCount - 1 - i;
            int rightPage = i;
            AddBookletSheet(builder, document, actualSheetWidth, actualSheetHeight, leftPage, rightPage, cellWidth, cellHeight, gutter);

            // Sheet back (e.g., pages 2 and 7)
            // For short-edge flip (duplex), the back side needs to match the orientation.
            // Actually, wait: short-edge flip means if we turn the page around the short edge, the top stays the top.
            // So we don't need to rotate the back pages 180 degrees.
            int backLeftPage = i + 1;
            int backRightPage = paddedCount - 2 - i;
            AddBookletSheet(builder, document, actualSheetWidth, actualSheetHeight, backLeftPage, backRightPage, cellWidth, cellHeight, gutter);
        }

        builder.Save(output, options, cancellationToken);
    }

    private static void AddBookletSheet(PdfDocumentBuilder builder, PdfDocument document, double sheetW, double sheetH, int leftPage, int rightPage, double cellW, double cellH, double gutter)
    {
        var layers = new List<(PdfDocument, int, (double, double, double, double, double, double))>();

        // Left page
        if (leftPage < document.PageCount)
        {
            var page = document.Pages[leftPage];
            var (w, h) = GetPageDimensions(document, page);
            double scale = Math.Min(cellW / w, cellH / h);
            double dx = gutter + (cellW - w * scale) / 2;
            double dy = gutter + (cellH - h * scale) / 2;
            layers.Add((document, leftPage, (scale, 0, 0, scale, dx, dy)));
        }

        // Right page
        if (rightPage < document.PageCount)
        {
            var page = document.Pages[rightPage];
            var (w, h) = GetPageDimensions(document, page);
            double scale = Math.Min(cellW / w, cellH / h);
            double dx = gutter * 2 + cellW + (cellW - w * scale) / 2;
            double dy = gutter + (cellH - h * scale) / 2;
            layers.Add((document, rightPage, (scale, 0, 0, scale, dx, dy)));
        }

        builder.AddComposedPage(sheetW, sheetH, layers);
    }

    private static (double Width, double Height) GetPageDimensions(PdfDocument document, PdfPage page)
    {
        int rotate = PdfDocument.NormalizeRotation(page.Rotate);
        double w = 0, h = 0;
        var bbox = (page.CropBox ?? page.MediaBox) as PdfArray;
        if (bbox != null && bbox.Items.Count >= 4)
        {
            double[] v = new double[4];
            for (int i = 0; i < 4; i++)
            {
                var item = document.Resolve(bbox.Items[i]);
                if (item is PdfInteger n) v[i] = n.Value;
                else if (item is PdfReal r) v[i] = r.Value;
            }
            w = v[2] - v[0];
            h = v[3] - v[1];
        }
        if (rotate == 90 || rotate == 270)
        {
            var tmp = w; w = h; h = tmp;
        }
        if (w <= 0) w = 612;
        if (h <= 0) h = 792;
        return (w, h);
    }
}
