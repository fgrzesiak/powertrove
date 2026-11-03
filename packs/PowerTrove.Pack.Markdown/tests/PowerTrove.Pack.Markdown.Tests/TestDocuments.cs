using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PowerTrove.Pack.Markdown.Tests;

/// <summary>Builds small Office, EPUB and PDF files in memory so the tests carry no binary fixtures.</summary>
internal static class TestDocuments
{
    public static byte[] Zip(params (string Path, object Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in entries)
            {
                using var stream = zip.CreateEntry(path).Open();
                byte[] bytes = content as byte[] ?? Encoding.UTF8.GetBytes((string)content);
                stream.Write(bytes);
            }
        return memory.ToArray();
    }

    private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" "
        + "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";

    private const string Rels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static byte[] Docx(string body, bool withNumbering = true) => Zip(
        ("_rels/.rels", $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{Rels}/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
        ("word/_rels/document.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + $"<Relationship Id=\"rIdStyles\" Type=\"{Rels}/styles\" Target=\"styles.xml\"/>"
            + (withNumbering ? $"<Relationship Id=\"rIdNum\" Type=\"{Rels}/numbering\" Target=\"numbering.xml\"/>" : "")
            + $"<Relationship Id=\"rIdNotes\" Type=\"{Rels}/footnotes\" Target=\"footnotes.xml\"/>"
            + $"<Relationship Id=\"rIdLink\" Type=\"{Rels}/hyperlink\" Target=\"https://example.com/a b\" TargetMode=\"External\"/>"
            + $"<Relationship Id=\"rIdImg\" Type=\"{Rels}/image\" Target=\"media/image1.png\"/></Relationships>"),
        ("word/styles.xml", $"<w:styles {W}>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"berschrift1\"><w:name w:val=\"heading 1\"/></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"berschrift2\"><w:name w:val=\"heading 2\"/><w:basedOn w:val=\"berschrift1\"/></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"Verzeichnis1\"><w:name w:val=\"toc 1\"/></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"Zitat\"><w:name w:val=\"Quote\"/></w:style></w:styles>"),
        ("word/numbering.xml", $"<w:numbering {W}>"
            + "<w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"bullet\"/></w:lvl><w:lvl w:ilvl=\"1\"><w:numFmt w:val=\"bullet\"/></w:lvl></w:abstractNum>"
            + "<w:abstractNum w:abstractNumId=\"1\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"decimal\"/></w:lvl></w:abstractNum>"
            + "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num><w:num w:numId=\"2\"><w:abstractNumId w:val=\"1\"/></w:num></w:numbering>"),
        ("word/footnotes.xml", $"<w:footnotes {W}><w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:t>---</w:t></w:r></w:p></w:footnote>"
            + "<w:footnote w:id=\"1\"><w:p><w:r><w:t>A source.</w:t></w:r></w:p></w:footnote></w:footnotes>"),
        ("word/media/image1.png", MarkdownActionTests.Png()),
        ("word/document.xml", $"<w:document {W}><w:body>{body}</w:body></w:document>"));

    public static string P(string runs, string? style = null, string? numbering = null) =>
        "<w:p>" + (style is null && numbering is null ? "" : "<w:pPr>" + (style is null ? "" : $"<w:pStyle w:val=\"{style}\"/>") + (numbering ?? "") + "</w:pPr>") + runs + "</w:p>";

    public static string R(string text, string? properties = null) =>
        "<w:r>" + (properties is null ? "" : "<w:rPr>" + properties + "</w:rPr>") + $"<w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(text)}</w:t></w:r>";

    public static string Num(int id, int level = 0) => $"<w:numPr><w:ilvl w:val=\"{level}\"/><w:numId w:val=\"{id}\"/></w:numPr>";

    public const string Picture = "<w:r><w:drawing><wp:inline><wp:docPr id=\"1\" name=\"Picture\" descr=\"A red dot\"/><a:graphic><a:graphicData><a:blip r:embed=\"rIdImg\"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

    public static byte[] Pptx(params string[] slides)
    {
        const string ns = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        var entries = new List<(string, object)>
        {
            ("_rels/.rels", $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{Rels}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>"),
            ("ppt/presentation.xml", $"<p:presentation {ns}><p:sldIdLst>" + string.Concat(slides.Select((_, i) => $"<p:sldId id=\"{256 + i}\" r:id=\"rIdS{i + 1}\"/>")) + "</p:sldIdLst></p:presentation>"),
            ("ppt/_rels/presentation.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + string.Concat(slides.Select((_, i) => $"<Relationship Id=\"rIdS{i + 1}\" Type=\"{Rels}/slide\" Target=\"slides/slide{i + 1}.xml\"/>")) + "</Relationships>"),
            ("ppt/media/image1.png", MarkdownActionTests.Png()),
            ("ppt/notesSlides/notesSlide1.xml", $"<p:notes {ns}><p:cSld><p:spTree><p:sp><p:nvSpPr><p:nvPr><p:ph type=\"body\"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>Say hello first.</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:notes>")
        };
        for (int i = 0; i < slides.Length; i++)
        {
            entries.Add(($"ppt/slides/slide{i + 1}.xml", $"<p:sld {ns}><p:cSld><p:spTree>{slides[i]}</p:spTree></p:cSld></p:sld>"));
            entries.Add(($"ppt/slides/_rels/slide{i + 1}.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + $"<Relationship Id=\"rIdImg\" Type=\"{Rels}/image\" Target=\"../media/image1.png\"/>"
                + (i == 0 ? $"<Relationship Id=\"rIdN\" Type=\"{Rels}/notesSlide\" Target=\"../notesSlides/notesSlide1.xml\"/>" : "") + "</Relationships>"));
        }
        return Zip([.. entries]);
    }

    public static string Shape(string? placeholder, long y, params string[] paragraphs) =>
        "<p:sp><p:nvSpPr><p:nvPr>" + (placeholder is null ? "" : $"<p:ph type=\"{placeholder}\"/>") + "</p:nvPr></p:nvSpPr>"
        + $"<p:spPr><a:xfrm><a:off x=\"0\" y=\"{y}\"/></a:xfrm></p:spPr><p:txBody>" + string.Concat(paragraphs) + "</p:txBody></p:sp>";

    public static string Para(string text, int level = 0, string? runProperties = null) =>
        $"<a:p><a:pPr lvl=\"{level}\"/><a:r>" + (runProperties is null ? "" : $"<a:rPr {runProperties}/>") + $"<a:t>{System.Security.SecurityElement.Escape(text)}</a:t></a:r></a:p>";

    /// <summary>A text PDF with the standard fonts; each line is (page, font, size, x, y, text).</summary>
    /// <param name="images">Pictures drawn as (page, x, y, width, height, picture); each picture is a 16x16 solid RGB color.</param>
    public static byte[] Pdf(int pages, IEnumerable<(int Page, string Font, double Size, double X, double Y, string Text)> lines, IEnumerable<(int Page, double X1, double Y1, double X2, double Y2)>? rules = null,
        IEnumerable<(int Page, double X, double Y, double Width, double Height, int Picture)>? images = null, IReadOnlyList<(byte R, byte G, byte B)>? pictures = null)
    {
        pictures ??= [];
        int firstPicture = 6 + pages * 2;
        var binary = new Dictionary<int, byte[]>();
        var objects = new List<string>();
        var fonts = new[] { ("F1", "Helvetica"), ("F2", "Helvetica-Bold"), ("F3", "Courier") };
        // 1 catalog, 2 pages, 3-5 fonts, then per page: page object and content stream.
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add("<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{6 + i * 2} 0 R")) + $"] /Count {pages} >>");
        foreach (var (_, name) in fonts) objects.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /{name} /Encoding /WinAnsiEncoding >>");
        for (int page = 1; page <= pages; page++)
        {
            var content = new StringBuilder();
            foreach (var line in lines.Where(l => l.Page == page))
                content.Append(CultureInfo.InvariantCulture, $"BT /{line.Font} {line.Size} Tf {line.X} {line.Y} Td ({line.Text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")}) Tj ET\n");
            foreach (var rule in (rules ?? []).Where(r => r.Page == page))
                content.Append(CultureInfo.InvariantCulture, $"{rule.X1} {rule.Y1} m {rule.X2} {rule.Y2} l S\n");
            foreach (var image in (images ?? []).Where(i => i.Page == page))
                content.Append(CultureInfo.InvariantCulture, $"q {image.Width} 0 0 {image.Height} {image.X} {image.Y} cm /Im{image.Picture} Do Q\n");
            string xobjects = pictures.Count == 0 ? "" : " /XObject << " + string.Join(" ", pictures.Select((_, i) => $"/Im{i} {firstPicture + i} 0 R")) + " >>";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R >>{xobjects} >> /Contents {7 + (page - 1) * 2} 0 R >>");
            byte[] stream = Encoding.Latin1.GetBytes(content.ToString());
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{content}endstream");
        }
        foreach (var (r, g, b) in pictures)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                for (int i = 0; i < 16 * 16; i++) zlib.Write([r, g, b]);
            binary[objects.Count] = compressed.ToArray();
            objects.Add($"<< /Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
        }
        // Latin-1 maps every byte to one character, so compressed data survives the string building unchanged.
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            string body = binary.TryGetValue(i, out var data) ? objects[i] + Encoding.Latin1.GetString(data) + "\nendstream" : objects[i];
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{body}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
