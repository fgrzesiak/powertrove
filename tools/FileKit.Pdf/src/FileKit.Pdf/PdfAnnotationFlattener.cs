using System.Globalization;
using System.Text;

namespace FileKit.Pdf;

public sealed record PdfFlattenReport(int Flattenable, int Discarded, int RetainedLinks);

/// <summary>Copies normal annotation appearances into ordinary page content.</summary>
public static class PdfAnnotationFlattener
{
    public static PdfFlattenReport Analyze(PdfDocument document, bool keepLinks = true)
    {
        int drawn = 0, dropped = 0, links = 0;
        foreach (var page in document.Pages)
        {
            var annots = document.Resolve(page.Dictionary["Annots"]) as PdfArray;
            if (annots is null) continue;
            if (annots.Count > 10000) throw new PdfLimitExceededException("Too many annotations.");
            foreach (var item in annots.Items)
            {
                var dict = document.Resolve(item) as PdfDictionary;
                if (dict?.GetName("Subtype") == "Link") { if (keepLinks) links++; }
                else if (dict?.GetName("Subtype") == "Popup") { }
                else if (dict is not null && Normal(document, dict) is PdfObject ap &&
                    document.Resolve(ap) is PdfStream stream &&
                    Array(document, dict["Rect"], 4) is not null &&
                    Array(document, stream.Dictionary["BBox"], 4) is not null) drawn++;
                else dropped++;
            }
        }
        return new(drawn, dropped, links);
    }

    internal static void Apply(PdfDocument source, PdfPage page, PdfDictionary target,
        PdfDocumentBuilder.ObjectCopier copier, PdfDocumentBuilder.ObjectTable table,
        bool keepLinks, CancellationToken token)
    {
        var annots = source.Resolve(page.Dictionary["Annots"]) as PdfArray;
        if (annots is null) return;
        if (annots.Count > 10000) throw new PdfLimitExceededException("Too many annotations.");
        var links = new PdfArray();
        var overlays = new StringBuilder();
        var resources = new PdfDictionary();
        if (source.Resolve(page.Resources) is PdfDictionary inherited)
            foreach (var (key, value) in inherited.Entries)
            {
                if (key == "XObject" && source.Resolve(value) is PdfDictionary xs)
                {
                    var cloned = new PdfDictionary();
                    foreach (var (name, entry) in xs.Entries) cloned[name] = copier.CopyValue(entry, 0);
                    resources[key] = cloned;
                }
                else resources[key] = copier.CopyValue(value, 0);
            }
        var xObjects = resources["XObject"] as PdfDictionary ?? new PdfDictionary();
        resources["XObject"] = xObjects;
        int counter = 0;
        foreach (var item in annots.Items)
        {
            token.ThrowIfCancellationRequested();
            if (source.Resolve(item) is not PdfDictionary dict) continue;
            string? subtype = dict.GetName("Subtype");
            if (subtype == "Link")
            {
                if (keepLinks) links.Items.Add(copier.CopyValue(item, 0));
                continue;
            }
            if (subtype == "Popup") continue;
            PdfObject? normal = Normal(source, dict);
            double[]? rect = Array(source, dict["Rect"], 4);
            if (normal is null || rect is null || source.Resolve(normal) is not PdfStream appearance) continue;
            double[]? bbox = Array(source, appearance.Dictionary["BBox"], 4);
            if (bbox is null) continue;
            double[] matrix = Array(source, appearance.Dictionary["Matrix"], 6) ?? [1, 0, 0, 1, 0, 0];
            var m = AppearancePlacement(rect, bbox, matrix);
            string name;
            do { name = "FKAp" + (++counter).ToString(CultureInfo.InvariantCulture); }
            while (xObjects.ContainsKey(name));
            PdfObject resource = copier.CopyValue(normal, 0);
            if (resource is not PdfReference)
            {
                // Form XObjects must be indirect stream objects, even if the
                // source annotation used an inline appearance stream.
                int resourceId = table.Allocate();
                table.Set(resourceId, resource);
                resource = new PdfReference(resourceId, 0);
            }
            xObjects[name] = resource;
            overlays.Append("q\n").Append(F(m.a)).Append(' ').Append(F(m.b)).Append(' ')
                .Append(F(m.c)).Append(' ').Append(F(m.d)).Append(' ')
                .Append(F(m.e)).Append(' ').Append(F(m.f)).Append(" cm\n/")
                .Append(name).Append(" Do\nQ\n");
        }
        if (links.Count > 0) target["Annots"] = links;
        else target.Remove("Annots");
        if (overlays.Length == 0) return;
        target["Resources"] = resources;
        var contents = new PdfArray();
        int q = table.Allocate();
        table.Set(q, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("q\n")));
        contents.Items.Add(new PdfReference(q, 0));
        if (source.Resolve(page.Dictionary["Contents"]) is PdfArray orig)
        {
            foreach (var entry in orig.Items) contents.Items.Add(copier.CopyValue(entry, 0));
        }
        else if (page.Dictionary["Contents"] is PdfObject original)
            contents.Items.Add(copier.CopyValue(original, 0));
        int Q = table.Allocate();
        table.Set(Q, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("Q\n")));
        contents.Items.Add(new PdfReference(Q, 0));
        int overlay = table.Allocate();
        table.Set(overlay, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(overlays.ToString())));
        contents.Items.Add(new PdfReference(overlay, 0));
        target["Contents"] = contents;
    }

    private static PdfObject? Normal(PdfDocument doc, PdfDictionary annotation)
    {
        if (doc.Resolve(annotation["AP"]) is not PdfDictionary appearance) return null;
        PdfObject? normal = appearance["N"];
        if (doc.Resolve(normal) is PdfStream) return normal;
        if (doc.Resolve(normal) is not PdfDictionary states ||
            annotation.GetName("AS") is not string state ||
            states[state] is not PdfObject chosen ||
            doc.Resolve(chosen) is not PdfStream) return null;
        return chosen;
    }

    private static double[]? Array(PdfDocument doc, PdfObject? value, int length)
    {
        if (doc.Resolve(value) is not PdfArray a || a.Count != length) return null;
        var result = new double[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = doc.Resolve(a[i]) switch
            {
                PdfInteger n => n.Value, PdfReal n => n.Value, _ => double.NaN
            };
            if (!double.IsFinite(result[i]) || Math.Abs(result[i]) > 1_000_000) return null;
        }
        return result;
    }

    /// <summary>ISO 32000 12.5.5: map Matrix-transformed BBox bounds to annotation Rect.</summary>
    public static (double a, double b, double c, double d, double e, double f) AppearancePlacement(
        double[] rect, double[] bbox, double[] matrix)
    {
        if (rect.Length != 4 || bbox.Length != 4 || matrix.Length != 6 ||
            rect.Concat(bbox).Concat(matrix).Any(v => !double.IsFinite(v)))
            throw new PdfOperationException("Unsupported annotation geometry.");
        var xs = new double[4]; var ys = new double[4];
        for (int i = 0; i < 4; i++)
        {
            double x = (i & 1) == 0 ? bbox[0] : bbox[2], y = i < 2 ? bbox[1] : bbox[3];
            xs[i] = matrix[0] * x + matrix[2] * y + matrix[4];
            ys[i] = matrix[1] * x + matrix[3] * y + matrix[5];
        }
        double minX = xs.Min(), maxX = xs.Max(), minY = ys.Min(), maxY = ys.Max();
        if (maxX-minX <= 1e-9 || maxY-minY <= 1e-9 || rect[2]-rect[0] <= 1e-9 || rect[3]-rect[1] <= 1e-9)
            throw new PdfOperationException("Invalid annotation dimensions.");
        double sx = (rect[2]-rect[0])/(maxX-minX), sy = (rect[3]-rect[1])/(maxY-minY);
        if (sx > 100000 || sy > 100000) throw new PdfLimitExceededException("Annotation transformation exceeds limit.");
        return (sx, 0, 0, sy, rect[0]-sx*minX, rect[1]-sy*minY);
    }
    private static string F(double v) => Math.Round(v, 8).ToString("0.########", CultureInfo.InvariantCulture);
}
