using System.Globalization;

namespace FileKit.Pdf;

/// <summary>Summary information without exposing document content.</summary>
public sealed record PdfDocumentInfo(int PageCount, string Version, bool WasRepaired, double FirstPageWidthPoints, double FirstPageHeightPoints);

/// <summary>
/// High-level page operations. Inputs are never modified; every method writes a new
/// document to the given output stream.
/// </summary>

public enum PdfWatermarkStyle { Diagonal, Horizontal, Tiled }

public static partial class PdfTools
{
    public static void AddWatermark(
        PdfDocument document,
        Stream output,
        string text,
        PdfWatermarkStyle style = PdfWatermarkStyle.Diagonal,
        double opacity = 0.2,
        double fontSize = 0,
        PdfColor? color = null,
        PdfWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(text)) throw new PdfOperationException("Watermark text cannot be empty.");

        var c = color ?? new PdfColor(0.5019, 0.5019, 0.5019); // #808080 default
        var builder = new PdfDocumentBuilder();
        int totalPages = document.PageCount;

        for (int i = 0; i < totalPages; i++)
        {
            var (pageW, pageH) = GetPageSize(document, i);
            double diagonal = Math.Sqrt(pageW * pageW + pageH * pageH);

            double actualFontSize = fontSize;
            if (fontSize <= 0)
            {
                double targetWidth = diagonal * 0.7;
                double baseWidth = PdfTextMeasurement.GetTextWidth(text, PdfStandardFont.HelveticaBold, 1.0);
                actualFontSize = Math.Max(1.0, targetWidth / baseWidth);
            }

            var decs = new List<PdfDecoration>();

            if (style == PdfWatermarkStyle.Diagonal)
            {
                double rotation = Math.Atan2(pageH, pageW) * 180.0 / Math.PI;
                decs.Add(new PdfTextDecoration(text, PdfAnchor.Center, actualFontSize, c, opacity, rotation, 0, PdfStandardFont.HelveticaBold));
            }
            else if (style == PdfWatermarkStyle.Horizontal)
            {
                decs.Add(new PdfTextDecoration(text, PdfAnchor.Center, actualFontSize, c, opacity, 0, 0, PdfStandardFont.HelveticaBold));
            }
            else if (style == PdfWatermarkStyle.Tiled)
            {
                double textWidth = PdfTextMeasurement.GetTextWidth(text, PdfStandardFont.HelveticaBold, actualFontSize);
                double textHeight = actualFontSize;

                double stepX = textWidth * 1.5;
                double stepY = textHeight * 3.0;

                int cols = (int)Math.Ceiling(pageW / stepX) + 1;
                int rows = (int)Math.Ceiling(pageH / stepY) + 1;

                int count = 0;
                double rotation = Math.Atan2(pageH, pageW) * 180.0 / Math.PI;

                for (int r = -rows; r <= rows * 2 && count < 200; r++)
                {
                    for (int col = -cols; col <= cols * 2 && count < 200; col++)
                    {
                        double offsetX = col * stepX + (r % 2 == 0 ? 0 : stepX / 2.0);
                        double offsetY = r * stepY;

                        decs.Add(new PdfTextDecoration(text, PdfAnchor.BottomLeft, actualFontSize, c, opacity, rotation, 0, PdfStandardFont.HelveticaBold, offsetX, offsetY));
                        count++;
                    }
                }
            }
            builder.AddPage(document, i, 0, decs);
        }
        builder.Save(output, options, cancellationToken);
    }

    public static PdfDocumentInfo GetInfo(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var (width, height) = GetPageSize(document, 0);
        return new PdfDocumentInfo(document.PageCount, document.Version, document.WasRepaired, width, height);
    }

    /// <summary>Visible page size in points (CropBox, else MediaBox), honoring rotation.</summary>
    public static (double Width, double Height) GetPageSize(PdfDocument document, int pageIndex)
    {
        var page = document.Pages[pageIndex];
        var box = document.Resolve(page.CropBox) as PdfArray ?? document.Resolve(page.MediaBox) as PdfArray;
        double width = 612, height = 792;
        if (box is { Count: 4 })
        {
            double[] v = box.Items.Select(i => document.Resolve(i) switch
            {
                PdfInteger n => n.Value,
                PdfReal r => r.Value,
                _ => 0d
            }).ToArray();
            width = Math.Abs(v[2] - v[0]);
            height = Math.Abs(v[3] - v[1]);
        }
        return page.Rotate % 180 == 0 ? (width, height) : (height, width);
    }

    public static void Merge(IReadOnlyList<PdfDocument> documents, Stream output, PdfWriteOptions? options = null, CancellationToken cancellationToken = default, IReadOnlyList<string>? fileBookmarkTitles = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0) throw new PdfOperationException("Nothing to merge.");

        var builder = new PdfDocumentBuilder();
        var combinedOutlines = new List<PdfOutlineItem>();
        int baseIndex = 0;

        for (int i = 0; i < documents.Count; i++)
        {
            var document = documents[i];

            if (fileBookmarkTitles != null && i < fileBookmarkTitles.Count)
            {
                var items = PdfOutline.Read(document, cancellationToken);
                var remapped = PdfOutline.Remap(items, p => p + baseIndex);
                combinedOutlines.Add(new PdfOutlineItem(fileBookmarkTitles[i], baseIndex, remapped));
            }

            builder.AddPages(document, Enumerable.Range(0, document.PageCount));
            baseIndex += document.PageCount;
        }

        if (fileBookmarkTitles != null)
        {
            builder.SetOutline(combinedOutlines);
        }

        builder.Save(output, options, cancellationToken);
    }

    /// <summary>Writes the given zero-based pages, in the given order.</summary>
    public static void ExtractPages(PdfDocument document, IReadOnlyList<int> pageIndices, Stream output, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (pageIndices.Count == 0) throw new PdfOperationException("No pages selected.");

        var builder = new PdfDocumentBuilder().AddPages(document, pageIndices);

        var items = PdfOutline.Read(document, cancellationToken);
        if (items.Count > 0)
        {
            var firstOccurrence = new Dictionary<int, int>();
            for (int i = 0; i < pageIndices.Count; i++)
            {
                if (!firstOccurrence.ContainsKey(pageIndices[i]))
                    firstOccurrence[pageIndices[i]] = i;
            }

            var mapped = PdfOutline.Remap(items, p => firstOccurrence.TryGetValue(p, out int newIdx) ? newIdx : null);
            builder.SetOutline(mapped);
        }

        builder.Save(output, options, cancellationToken);
    }

    /// <summary>Writes the document without the given zero-based pages.</summary>
    public static void RemovePages(PdfDocument document, IReadOnlyCollection<int> pageIndices, Stream output, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var remove = pageIndices.ToHashSet();
        var keep = Enumerable.Range(0, document.PageCount).Where(i => !remove.Contains(i)).ToList();
        if (keep.Count == 0) throw new PdfOperationException("All pages would be removed.");

        var builder = new PdfDocumentBuilder().AddPages(document, keep);

        var items = PdfOutline.Read(document, cancellationToken);
        if (items.Count > 0)
        {
            var firstOccurrence = new Dictionary<int, int>();
            for (int i = 0; i < keep.Count; i++)
            {
                if (!firstOccurrence.ContainsKey(keep[i]))
                    firstOccurrence[keep[i]] = i;
            }

            var mapped = PdfOutline.Remap(items, p => firstOccurrence.TryGetValue(p, out int newIdx) ? newIdx : null);
            builder.SetOutline(mapped);
        }

        builder.Save(output, options, cancellationToken);
    }

    /// <summary>Rotates the given pages (all when null) clockwise by a multiple of 90 degrees.</summary>
    public static void Rotate(PdfDocument document, int degrees, IReadOnlyCollection<int>? pageIndices, Stream output, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (degrees % 90 != 0) throw new PdfOperationException("Rotation must be a multiple of 90 degrees.");
        var selected = pageIndices?.ToHashSet();
        var builder = new PdfDocumentBuilder();
        for (int i = 0; i < document.PageCount; i++)
            builder.AddPage(document, i, selected is null || selected.Contains(i) ? degrees : 0);

        var items = PdfOutline.Read(document, cancellationToken);
        if (items.Count > 0)
        {
            // Rotate keeps all pages, so identity mapping
            var mapped = PdfOutline.Remap(items, p => p);
            builder.SetOutline(mapped);
        }

        builder.Save(output, options, cancellationToken);
    }

    /// <summary>Rewrites the document without document info, XMP and page metadata.</summary>
        public static void Crop(PdfDocument document, double marginMm, IReadOnlyCollection<int>? pageIndices, Stream output, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var selected = pageIndices?.ToHashSet();
        var builder = new PdfDocumentBuilder();
        double marginPt = marginMm * 72.0 / 25.4;

        for (int i = 0; i < document.PageCount; i++)
        {
            if (selected is null || selected.Contains(i))
            {
                var page = document.Pages[i];
                var mediaBox = document.Resolve(page.MediaBox) as PdfArray;
                var cropBox = document.Resolve(page.CropBox) as PdfArray ?? mediaBox;

                if (cropBox is { Count: 4 })
                {
                    double[] c = cropBox.Items.Select(item => document.Resolve(item) switch
                    {
                        PdfInteger n => n.Value,
                        PdfReal r => r.Value,
                        _ => 0d
                    }).ToArray();

                    double minX = c[0], minY = c[1], maxX = c[2], maxY = c[3];
                    if (minX > maxX) (minX, maxX) = (maxX, minX);
                    if (minY > maxY) (minY, maxY) = (maxY, minY);

                    double newMinX = minX + marginPt;
                    double newMinY = minY + marginPt;
                    double newMaxX = maxX - marginPt;
                    double newMaxY = maxY - marginPt;

                    if (newMinX >= newMaxX)
                    {
                        newMinX = (minX + maxX) / 2.0 - 0.1;
                        newMaxX = (minX + maxX) / 2.0 + 0.1;
                    }
                    if (newMinY >= newMaxY)
                    {
                        newMinY = (minY + maxY) / 2.0 - 0.1;
                        newMaxY = (minY + maxY) / 2.0 + 0.1;
                    }

                    if (mediaBox is { Count: 4 })
                    {
                        double[] m = mediaBox.Items.Select(item => document.Resolve(item) switch
                        {
                            PdfInteger n => n.Value,
                            PdfReal r => r.Value,
                            _ => 0d
                        }).ToArray();
                        double mMinX = Math.Min(m[0], m[2]);
                        double mMaxX = Math.Max(m[0], m[2]);
                        double mMinY = Math.Min(m[1], m[3]);
                        double mMaxY = Math.Max(m[1], m[3]);

                        newMinX = Math.Max(newMinX, mMinX);
                        newMinY = Math.Max(newMinY, mMinY);
                        newMaxX = Math.Min(newMaxX, mMaxX);
                        newMaxY = Math.Min(newMaxY, mMaxY);
                    }

                    page.Dictionary["CropBox"] = new PdfArray(new PdfObject[] { new PdfReal(newMinX), new PdfReal(newMinY), new PdfReal(newMaxX), new PdfReal(newMaxY) });
                }
            }
            builder.AddPage(document, i, 0);
        }
        builder.Save(output, options, cancellationToken);
    }

    public static void ResizePages(PdfDocument document, string size, string fit, Stream output, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new PdfDocumentBuilder();

        double targetW = size switch
        {
            "letter" => 612.0,
            "a5" => 419.53,
            "legal" => 612.0,
            _ => 595.28 // a4
        };
        double targetH = size switch
        {
            "letter" => 792.0,
            "a5" => 595.28,
            "legal" => 1008.0,
            _ => 841.89 // a4
        };

        for (int i = 0; i < document.PageCount; i++)
        {
            var (origW, origH) = GetPageSize(document, i);
            double scale = Math.Min(targetW / origW, targetH / origH);
            if (fit == "fit-shrink" && scale > 1.0)
            {
                scale = 1.0;
            }

            double finalW = origW * scale;
            double finalH = origH * scale;

            double offsetX = (targetW - finalW) / 2.0;
            double offsetY = (targetH - finalH) / 2.0;

            var layers = new List<(PdfDocument document, int pageIndex, (double a, double b, double c, double d, double e, double f) matrix)>
            {
                (document, i, (scale, 0, 0, scale, offsetX, offsetY))
            };

            builder.AddComposedPage(targetW, targetH, layers);
        }
        builder.Save(output, options, cancellationToken);
    }

    /// <summary>Splits the document into individual pages.</summary>
    public static void Split(PdfDocument document, Func<int, Stream> outputStreamFactory, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(outputStreamFactory);

        for (int i = 0; i < document.PageCount; i++)
        {
            using var output = outputStreamFactory(i);
            ExtractPages(document, new[] { i }, output, options, cancellationToken);
        }
    }

    public static void RemoveMetadata(PdfDocument document, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        new PdfDocumentBuilder()
            .AddPages(document, Enumerable.Range(0, document.PageCount))
            .Save(output, new PdfWriteOptions { RemoveMetadata = true, Producer = null }, cancellationToken);
    }

    public static void EditMetadata(PdfDocument document, Stream output, string? title, string? author, string? subject, string? keywords, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        new PdfDocumentBuilder()
            .AddPages(document, Enumerable.Range(0, document.PageCount))
            .Save(output, new PdfWriteOptions
            {
                EditMetadata = true,
                Title = title,
                Author = author,
                Subject = subject,
                Keywords = keywords
            }, cancellationToken);
    }

    public static void Flatten(PdfDocument document, Stream output, bool keepLinks = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        new PdfDocumentBuilder()
            .AddPages(document, Enumerable.Range(0, document.PageCount))
            .Save(output, new PdfWriteOptions { FlattenAnnotations = true, KeepFlattenedLinks = keepLinks }, cancellationToken);
    }

    public static void Sanitize(PdfDocument document, Stream output, bool keepLinks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var flags = PdfSanitizeFlags.Scripts | PdfSanitizeFlags.Attachments | PdfSanitizeFlags.Metadata;
        flags |= PdfSanitizeFlags.LaunchActions;
        if (!keepLinks) flags |= PdfSanitizeFlags.UriActions;

        new PdfDocumentBuilder()
            .AddPages(document, Enumerable.Range(0, document.PageCount))
            .Save(output, new PdfWriteOptions { Sanitize = flags, Producer = null }, cancellationToken);
    }

    public static void Repair(PdfDocument document, Stream output, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= PdfWriteOptions.Default;

        var writer = new PdfDocumentBuilder.ObjectTable(options);
        var rootRef = document.Trailer["Root"] as PdfReference;
        var infoRef = document.Trailer["Info"] as PdfReference;

        var pagesMap = new Dictionary<(Guid, int), int>();
        var replacements = new Dictionary<(Guid, int), PdfImageReplacement>();

        // Repair copies the original catalog and page tree, rather than rebuilding
        // a selection of pages as the other transformations do.
        var copier = new PdfDocumentBuilder.ObjectCopier(document, writer, pagesMap, options, replacements, preservePageTree: true);

        PdfReference? rootCopy;
        if (rootRef is not null)
        {
            rootCopy = copier.CopyValue(rootRef, 0) as PdfReference;
        }
        else
        {
            // A reconstructed document can recover its catalog without a trailer.
            int rootNumber = writer.Allocate();
            writer.Set(rootNumber, copier.CopyValue(document.Catalog, 0));
            rootCopy = new PdfReference(rootNumber, 0);
        }
        var infoCopy = infoRef != null ? copier.CopyValue(infoRef, 0) as PdfReference : null;

        copier.Drain(cancellationToken);

        if (rootCopy == null) throw new PdfOperationException("Failed to copy root catalog.");
        writer.Write(output, rootCopy, infoCopy, cancellationToken);
    }

    public static void ImagesToPdf(IReadOnlyList<PdfImage> images, Stream output, PdfImagePageLayout? layout = null, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0) throw new PdfOperationException("No images selected.");
        var builder = new PdfDocumentBuilder();
        foreach (var image in images) builder.AddImagePage(image, layout);
        builder.Save(output, options, cancellationToken);
    }

    /// <summary>Combines two documents by alternately taking pages from each.</summary>
    public static void Interleave(PdfDocument first, PdfDocument second, bool reverseSecond, Stream output, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var builder = new PdfDocumentBuilder();
        int n1 = first.PageCount;
        int n2 = second.PageCount;
        int max = Math.Max(n1, n2);

        for (int i = 0; i < max; i++)
        {
            if (i < n1)
            {
                builder.AddPage(first, i);
            }
            if (i < n2)
            {
                int index2 = reverseSecond ? n2 - 1 - i : i;
                builder.AddPage(second, index2);
            }
        }
        builder.Save(output, options, cancellationToken);
    }

    /// <summary>Inserts the source document into the target document at the given position.</summary>
    public static void Insert(PdfDocument source, PdfDocument target, int positionIndex, Stream output, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var builder = new PdfDocumentBuilder();
        int nTarget = target.PageCount;
        int nSource = source.PageCount;

        if (positionIndex < -1 || positionIndex > nTarget)
            throw new PdfOperationException($"Position index must be between -1 and {nTarget}.");

        for (int i = 0; i < nTarget; i++)
        {
            if (i == positionIndex + 1)
            {
                for (int j = 0; j < nSource; j++)
                    builder.AddPage(source, j);
            }
            builder.AddPage(target, i);
        }

        if (positionIndex >= nTarget - 1)
        {
            for (int j = 0; j < nSource; j++)
                builder.AddPage(source, j);
        }

        builder.Save(output, options, cancellationToken);
    }

    public static void StampImage(
        PdfDocument document,
        PdfImage image,
        Stream output,
        PdfAnchor position = PdfAnchor.Center,
        double widthMm = 40.0,
        double opacity = 1.0,
        string pages = "all",
        PdfWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(image);

        IReadOnlyList<int> pageIndices;
        if (string.Equals(pages, "all", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(pages))
        {
            pageIndices = Enumerable.Range(0, document.PageCount).ToList();
        }
        else if (string.Equals(pages, "first", StringComparison.OrdinalIgnoreCase))
        {
            pageIndices = new[] { 0 };
        }
        else if (string.Equals(pages, "last", StringComparison.OrdinalIgnoreCase))
        {
            pageIndices = new[] { document.PageCount - 1 };
        }
        else
        {
            pageIndices = PageRange.Parse(pages, document.PageCount, allowDuplicates: false);
        }

        var selectedPages = new HashSet<int>(pageIndices);
        double widthPt = widthMm * 72.0 / 25.4;

        var builder = new PdfDocumentBuilder();
        for (int i = 0; i < document.PageCount; i++)
        {
            if (selectedPages.Contains(i))
            {
                var dec = new PdfImageDecoration(image, position, widthPt, opacity);
                builder.AddPage(document, i, 0, new[] { dec });
            }
            else
            {
                builder.AddPage(document, i);
            }
        }

        builder.Save(output, options, cancellationToken);
    }



    public static void ValidateWinAnsi(char c)
    {
        PdfTextMeasurement.GetWinAnsiCode(c);
    }

    public static void AddHeaderFooter(
        PdfDocument document,
        Stream output,
        string textTemplate,
        PdfAnchor position,
        int fontSize,
        string dateString,
        PdfWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(textTemplate);

        var builder = new PdfDocumentBuilder();
        int totalPages = document.PageCount;

        for (int i = 0; i < totalPages; i++)
        {
            string text = textTemplate
                .Replace("{page}", (i + 1).ToString())
                .Replace("{pages}", totalPages.ToString())
                .Replace("{date}", dateString);

            var dec = new PdfTextDecoration(text, position, fontSize, new PdfColor(0, 0, 0));
            builder.AddPage(document, i, 0, new[] { dec });
        }

        builder.Save(output, options, cancellationToken);
    }

    public static void AddPageNumbers(
        PdfDocument document,
        Stream output,
        PdfAnchor position = PdfAnchor.BottomCenter,
        PdfPageNumberFormat format = PdfPageNumberFormat.Number,
        int startAt = 1,
        bool skipFirst = false,
        int fontSize = 10,
        PdfWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var builder = new PdfDocumentBuilder();
        int totalPages = document.PageCount;
        int currentNumber = startAt;

        for (int i = 0; i < totalPages; i++)
        {
            if (skipFirst && i == 0)
            {
                builder.AddPage(document, i);
                continue;
            }

            string text = format switch
            {
                PdfPageNumberFormat.PageNumberOfTotal => $"Page {currentNumber} of {totalPages}",
                PdfPageNumberFormat.NumberSlashTotal => $"{currentNumber} / {totalPages}",
                _ => currentNumber.ToString()
            };

            var dec = new PdfTextDecoration(text, position, fontSize, new PdfColor(0, 0, 0));
            builder.AddPage(document, i, 0, new[] { dec });
            currentNumber++;
        }

        builder.Save(output, options, cancellationToken);
    }

    /// <summary>Reorders the pages of the document. The given order must be a full permutation.</summary>
    public static void Reorder(PdfDocument document, IReadOnlyList<int> order, Stream output, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(order);

        int n = document.PageCount;
        var seen = new HashSet<int>();
        foreach (int p in order)
        {
            if (p < 0 || p >= n) throw new PdfOperationException($"Page numbers must be between 1 and {n}.");
            if (!seen.Add(p)) throw new PdfOperationException($"Page {p + 1} is listed twice.");
        }
        for (int i = 0; i < n; i++)
        {
            if (!seen.Contains(i)) throw new PdfOperationException($"Page {i + 1} is missing.");
        }

        var builder = new PdfDocumentBuilder().AddPages(document, order);

        var items = PdfOutline.Read(document, cancellationToken);
        if (items.Count > 0)
        {
            var firstOccurrence = new Dictionary<int, int>();
            for (int i = 0; i < order.Count; i++)
            {
                if (!firstOccurrence.ContainsKey(order[i]))
                    firstOccurrence[order[i]] = i;
            }

            var mapped = PdfOutline.Remap(items, p => firstOccurrence.TryGetValue(p, out int newIdx) ? newIdx : null);
            builder.SetOutline(mapped);
        }

        builder.Save(output, options, cancellationToken);
    }
}

public enum PdfPageNumberFormat
{
    Number,
    PageNumberOfTotal,
    NumberSlashTotal
}

/// <summary>Parses user page ranges such as "1-3, 5, 8-" (1-based, inclusive).</summary>
public static class PageRange
{
    public const int MaxRangeTextLength = 1000;

    public static bool IsValidSyntax(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length > MaxRangeTextLength) return false;

        var parts = text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        foreach (var rawPart in parts)
        {
            string part = rawPart.Trim();
            if (part.Length == 0) continue;

            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!IsPageNumberOrLast(part)) return false;
            }
            else
            {
                string left = part[..dash].Trim();
                string right = part[(dash + 1)..].Trim();

                if (left.Length > 0 && !IsPageNumberOrLast(left)) return false;
                if (right.Length > 0 && !IsPageNumberOrLast(right)) return false;
            }
        }
        return true;
    }

    private static bool IsPageNumberOrLast(string value)
    {
        if (string.Equals(value, "last", StringComparison.OrdinalIgnoreCase)) return true;
        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int page) && page >= 1;
    }

    /// <summary>Returns zero-based page indices in the given order without duplicates.</summary>
    public static IReadOnlyList<int> Parse(string text, int pageCount, bool allowDuplicates = false, CancellationToken cancellationToken = default)
    {
        if (!TryParse(text, pageCount, out var pages, out var error, allowDuplicates, cancellationToken))
            throw new PdfOperationException(error!);
        return pages;
    }

    public static bool TryParse(string? text, int pageCount, out IReadOnlyList<int> pages, out string? error, bool allowDuplicates = false, CancellationToken cancellationToken = default)
    {
        pages = Array.Empty<int>();
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter the pages, for example 1-3, 5.";
            return false;
        }
        if (text.Length > MaxRangeTextLength)
        {
            error = "The page selection is too long.";
            return false;
        }

        int limit = Math.Min(pageCount, 100000);
        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var rawPart in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string part = rawPart.Trim();
            if (part.Length == 0) continue;

            int first, last;
            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!TryPage(part, limit, out first)) return Fail(part, limit, out error);
                last = first;
            }
            else
            {
                string left = part[..dash].Trim();
                string right = part[(dash + 1)..].Trim();
                if (left.Length == 0) first = 1;
                else if (!TryPage(left, limit, out first)) return Fail(part, limit, out error);
                if (right.Length == 0) last = limit;
                else if (!TryPage(right, limit, out last)) return Fail(part, limit, out error);
                if (last < first)
                {
                    error = "A page range must go from a lower to a higher page number.";
                    return false;
                }
            }

            for (int p = first; p <= last; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (allowDuplicates)
                {
                    result.Add(p - 1);
                }
                else
                {
                    if (seen.Add(p - 1)) result.Add(p - 1);
                }
            }
        }

        if (result.Count == 0)
        {
            error = "Enter the pages, for example 1-3, 5.";
            return false;
        }

        pages = result;
        return true;
    }

    private static bool TryPage(string value, int pageCount, out int page)
    {
        if (string.Equals(value, "last", StringComparison.OrdinalIgnoreCase))
        {
            page = pageCount;
            return true;
        }
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out page) && page >= 1 && page <= pageCount;
    }

    private static bool Fail(string part, int pageCount, out string? error)
    {
        error = $"Page numbers must be between 1 and {pageCount}.";
        return false;
    }
}
