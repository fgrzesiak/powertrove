using System;
using System.Collections.Generic;

namespace FileKit.Pdf;

[Flags]
public enum PdfSanitizeFlags
{
    None = 0,
    Scripts = 1 << 0,
    Attachments = 1 << 1,
    LaunchActions = 1 << 2,
    UriActions = 1 << 3,
    Metadata = 1 << 4
}

public readonly record struct PdfSanitizeCounts(int Scripts, int Attachments, int LaunchActions, int UriActions, int MetadataItems)
{
    public bool HasHiddenContent => Scripts > 0 || Attachments > 0 || LaunchActions > 0 || UriActions > 0 || MetadataItems > 0;
}

public static class PdfSanitizer
{
    public static PdfSanitizeCounts Analyze(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        int scripts = 0;
        int attachments = 0;
        int launchActions = 0;
        int uriActions = 0;
        int metadataItems = 0;

        var visited = new HashSet<int>();

        var catalog = document.Catalog;

        if (catalog["Names"] is PdfReference namesRef && document.Resolve(namesRef) is PdfDictionary namesDict)
        {
            if (namesDict["JavaScript"] != null) scripts++;
            if (namesDict["EmbeddedFiles"] != null) attachments++;
        }
        else if (catalog["Names"] is PdfDictionary inlineNames)
        {
            if (inlineNames["JavaScript"] != null) scripts++;
            if (inlineNames["EmbeddedFiles"] != null) attachments++;
        }

        if (catalog["OpenAction"] is PdfDictionary openActionDict && openActionDict.GetName("S") == "JavaScript")
            scripts++;

        if (catalog["AA"] != null) scripts++;
        if (catalog["Metadata"] != null) metadataItems++;
        if (catalog["PieceInfo"] != null) metadataItems++;

        if (document.Info != null)
        {
            metadataItems++;
        }

        void VisitAnnotation(PdfDictionary annotDict)
        {
            string? subtype = annotDict.GetName("Subtype");
            if (subtype == "FileAttachment")
            {
                attachments++;
            }

            var aa = document.Resolve(annotDict["AA"]) as PdfDictionary;
            if (aa != null)
            {
                scripts++;
            }

            var a = document.Resolve(annotDict["A"]) as PdfDictionary;
            if (a != null)
            {
                string? s = a.GetName("S");
                if (s == "JavaScript") scripts++;
                else if (s == "Launch") launchActions++;
                else if (s == "URI") uriActions++;
            }
        }

        for (int i = 0; i < document.PageCount; i++)
        {
            var page = document.Pages[i];

            if (page.Dictionary["AA"] != null) scripts++;
            if (page.Dictionary["Metadata"] != null) metadataItems++;
            if (page.Dictionary["PieceInfo"] != null) metadataItems++;

            var annots = document.Resolve(page.Dictionary["Annots"]) as PdfArray;
            if (annots != null)
            {
                foreach (var annotObj in annots.Items)
                {
                    var resolvedAnnot = document.Resolve(annotObj);
                    if (resolvedAnnot is PdfDictionary annotDict)
                    {
                        if (annotObj is PdfReference annotRef)
                        {
                            if (!visited.Add(annotRef.ObjectNumber)) continue;
                        }
                        VisitAnnotation(annotDict);
                    }
                }
            }
        }

        return new PdfSanitizeCounts(scripts, attachments, launchActions, uriActions, metadataItems);
    }
}
