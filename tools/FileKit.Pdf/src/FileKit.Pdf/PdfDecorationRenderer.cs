using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FileKit.Pdf;

internal static class PdfDecorationRenderer
{
    public static void ApplyDecorations(
        PdfDictionary pageCopy,
        PdfArray visibleBox,
        int pageRotate,
        IReadOnlyList<PdfDecoration> decorations,
        PdfDocumentBuilder.ObjectTable table,
        PdfDocumentBuilder.ObjectCopier copier,
        Dictionary<PdfImage, int>? imageCache = null,
        PdfDocument? sourceDoc = null)
    {
        if (decorations.Count == 0) return;

        double[] box = visibleBox.Items.Select(x =>
            x is PdfReal r ? r.Value : (x as PdfInteger)?.Value ?? 0.0).ToArray();

        if (box.Length < 4) box = new double[] { 0, 0, 612, 792 };

        double boxX = box[0];
        double boxY = box[1];
        double boxW = box[2] - box[0];
        double boxH = box[3] - box[1];

        PdfDictionary resources;
        if (pageCopy["Resources"] is PdfDictionary d)
        {
            resources = d;
        }
        else if (pageCopy["Resources"] is PdfReference r)
        {
            // The PdfDocumentBuilder has already resolved original indirect resources into a direct dictionary
            // if decorations are applied. If we get here, just allocate a new one.
            resources = new PdfDictionary();
            pageCopy["Resources"] = resources;
        }
        else
        {
            resources = new PdfDictionary();
            pageCopy["Resources"] = resources;
        }

        PdfDictionary fontDict = GetOrCreateDict(resources, "Font");
        PdfDictionary extGStateDict = GetOrCreateDict(resources, "ExtGState");
        PdfDictionary xObjectDict = GetOrCreateDict(resources, "XObject");

        var sb = new StringBuilder();

        int fontCounter = 1;
        int stateCounter = 1;
        int imgCounter = 1;

        foreach (var dec in decorations)
        {
            if (dec is PdfTextDecoration textDec)
            {
                double opacity = Math.Clamp(textDec.Opacity, 0, 1);

                while (extGStateDict.Entries.Any(e => e.Key == $"FKGs{stateCounter}")) stateCounter++;
                string stateName = $"FKGs{stateCounter}";
                stateCounter++;

                extGStateDict[stateName] = new PdfDictionary
                {
                    ["Type"] = new PdfName("ExtGState"),
                    ["ca"] = new PdfReal(opacity),
                    ["CA"] = new PdfReal(opacity)
                };

                while (fontDict.Entries.Any(e => e.Key == $"FKFont{fontCounter}")) fontCounter++;
                string fontName = $"FKFont{fontCounter}";
                fontCounter++;

                string baseFont = textDec.Font switch
                {
                    PdfStandardFont.Helvetica => "Helvetica",
                    PdfStandardFont.HelveticaBold => "Helvetica-Bold",
                    PdfStandardFont.TimesRoman => "Times-Roman",
                    PdfStandardFont.Courier => "Courier",
                    _ => "Helvetica"
                };

                fontDict[fontName] = new PdfDictionary
                {
                    ["Type"] = new PdfName("Font"),
                    ["Subtype"] = new PdfName("Type1"),
                    ["BaseFont"] = new PdfName(baseFont),
                    ["Encoding"] = new PdfName("WinAnsiEncoding")
                };

                double objW = PdfTextMeasurement.GetTextWidth(textDec.Text, textDec.Font, textDec.FontSizePt);
                double objH = textDec.FontSizePt; // approximation for layout

                var (a, b, c, dMatrix, e, f) = PdfPlacement.GetMatrix(
                    boxX, boxY, boxW, boxH, pageRotate, textDec.Anchor, objW, objH, textDec.MarginPt, textDec.RotationDegrees, textDec.OffsetXPt, textDec.OffsetYPt);

                sb.Append("q\n");
                sb.Append($"/{stateName} gs\n");
                sb.Append($"{Format(textDec.Color.R)} {Format(textDec.Color.G)} {Format(textDec.Color.B)} rg\n");
                sb.Append($"{Format(textDec.Color.R)} {Format(textDec.Color.G)} {Format(textDec.Color.B)} RG\n");
                sb.Append("BT\n");
                if (textDec.Invisible)
                {
                    sb.Append("3 Tr\n");
                }
                if (textDec.HorizontalScaling.HasValue)
                {
                    sb.Append($"{Format(textDec.HorizontalScaling.Value)} Tz\n");
                }
                sb.Append($"/{fontName} {Format(textDec.FontSizePt)} Tf\n");
                sb.Append($"{Format(a)} {Format(b)} {Format(c)} {Format(dMatrix)} {Format(e)} {Format(f)} Tm\n");

                byte[] textBytes = textDec.Text.Select(c => PdfTextMeasurement.GetWinAnsiCode(c)).ToArray();
                sb.Append("<");
                foreach (byte bVal in textBytes)
                {
                    sb.Append(bVal.ToString("X2"));
                }
                sb.Append("> Tj\n");

                sb.Append("ET\n");
                sb.Append("Q\n");
            }
            else if (dec is PdfImageDecoration imgDec)
            {
                double opacity = Math.Clamp(imgDec.Opacity, 0, 1);

                while (extGStateDict.Entries.Any(e => e.Key == $"FKGs{stateCounter}")) stateCounter++;
                string stateName = $"FKGs{stateCounter}";
                stateCounter++;

                extGStateDict[stateName] = new PdfDictionary
                {
                    ["Type"] = new PdfName("ExtGState"),
                    ["ca"] = new PdfReal(opacity),
                    ["CA"] = new PdfReal(opacity)
                };

                while (xObjectDict.Entries.Any(e => e.Key == $"FKIm{imgCounter}")) imgCounter++;
                string imgName = $"FKIm{imgCounter}";
                imgCounter++;

                int imgRefNum;
                if (imageCache != null && imageCache.TryGetValue(imgDec.Image, out var cachedRefNum))
                {
                    imgRefNum = cachedRefNum;
                }
                else
                {
                    imgRefNum = table.Allocate();
                    PdfStream xObject = imgDec.Image.CreateXObject(table);
                    table.Set(imgRefNum, xObject);
                    if (imageCache != null) imageCache[imgDec.Image] = imgRefNum;
                }

                xObjectDict[imgName] = new PdfReference(imgRefNum, 0);

                double objW = imgDec.WidthPt;
                double objH = (double)imgDec.Image.Height / imgDec.Image.Width * objW;

                var (a, b, c, dMatrix, e, f) = PdfPlacement.GetMatrix(
                    boxX, boxY, boxW, boxH, pageRotate, imgDec.Anchor, objW, objH, imgDec.MarginPt);

                sb.Append("q\n");
                sb.Append($"/{stateName} gs\n");
                sb.Append($"{Format(a * objW)} {Format(b * objW)} {Format(c * objH)} {Format(dMatrix * objH)} {Format(e)} {Format(f)} cm\n");
                sb.Append($"/{imgName} Do\n");
                sb.Append("Q\n");
            }
        }

        PdfObject origContents = pageCopy["Contents"] ?? PdfNull.Instance;

        int qRef = table.Allocate();
        table.Set(qRef, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("q\n")));

        int QRef = table.Allocate();
        table.Set(QRef, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("Q\n")));

        int overlayRef = table.Allocate();
        table.Set(overlayRef, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(sb.ToString())));

        var newContents = new PdfArray();
        newContents.Items.Add(new PdfReference(qRef, 0));

        if (origContents is PdfArray arr)
        {
            foreach (var item in arr.Items) newContents.Items.Add(item);
        }
        else if (origContents is PdfReference r)
        {
            newContents.Items.Add(r);
        }

        newContents.Items.Add(new PdfReference(QRef, 0));
        newContents.Items.Add(new PdfReference(overlayRef, 0));

        pageCopy["Contents"] = newContents;
    }

    private static PdfDictionary GetOrCreateDict(PdfDictionary parent, string key)
    {
        if (parent[key] is PdfDictionary d) return d;
        var newDict = new PdfDictionary();
        parent[key] = newDict;
        return newDict;
    }

    private static string Format(double value) => Math.Round(value, 4).ToString(CultureInfo.InvariantCulture);
}
