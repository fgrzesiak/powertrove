using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace FileKit.Xlsx;

public static class XlsxWriter
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static Task WriteAsync(Stream output, Workbook workbook, CancellationToken cancellationToken = default) =>
        Task.Run(() => Write(output, workbook, cancellationToken), cancellationToken);
    private static void Write(Stream output, Workbook workbook, CancellationToken ct)
    {
        if (workbook.Sheets.Count is < 1 or > 256) throw new XlsxException("Invalid sheet count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long cells = 0;
        foreach (var sheet in workbook.Sheets)
        {
            ct.ThrowIfCancellationRequested();
            var check = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (SheetNames.Unique(sheet.Name, check) != sheet.Name || !names.Add(sheet.Name) || sheet.Rows.Count > 1048576) throw new XlsxException("Invalid sheet name or size.");
            foreach (var row in sheet.Rows)
            {
                if (row.Count > 16384 || (cells += row.Count) > 4000000) throw new XlsxException("The workbook exceeds supported limits.");
                foreach (var cell in row)
                    if (cell is NumberCell n && !double.IsFinite(n.Value) || cell is TextCell t && t.Value.Length > 32767) throw new XlsxException("Invalid cell value.");
            }
        }
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Part(string path, Action<XmlWriter> write)
        {
            ct.ThrowIfCancellationRequested();
            using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
            using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), CloseOutput = false });
            xml.WriteStartDocument(); write(xml); xml.WriteEndDocument();
        }
        Part("[Content_Types].xml", x =>
        {
            const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
            x.WriteStartElement("Types", ns);
            void Type(string element, string key, string name, string type) { x.WriteStartElement(element, ns); x.WriteAttributeString(key, name); x.WriteAttributeString("ContentType", type); x.WriteEndElement(); }
            Type("Default", "Extension", "rels", "application/vnd.openxmlformats-package.relationships+xml");
            Type("Default", "Extension", "xml", "application/xml");
            Type("Override", "PartName", "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            Type("Override", "PartName", "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            for (int i = 0; i < workbook.Sheets.Count; i++) Type("Override", "PartName", $"/xl/worksheets/sheet{i + 1}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            x.WriteEndElement();
        });
        void Relationship(XmlWriter x, string id, string kind, string target)
        {
            x.WriteStartElement("Relationship", "http://schemas.openxmlformats.org/package/2006/relationships");
            x.WriteAttributeString("Id", id); x.WriteAttributeString("Type", Rel + "/" + kind); x.WriteAttributeString("Target", target); x.WriteEndElement();
        }
        Part("_rels/.rels", x => { x.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships"); Relationship(x, "rId1", "officeDocument", "xl/workbook.xml"); x.WriteEndElement(); });
        Part("xl/workbook.xml", x =>
        {
            x.WriteStartElement("workbook", Main); x.WriteAttributeString("xmlns", "r", null, Rel); x.WriteStartElement("sheets", Main);
            for (int i = 0; i < workbook.Sheets.Count; i++)
            {
                x.WriteStartElement("sheet", Main); x.WriteAttributeString("name", workbook.Sheets[i].Name); x.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                x.WriteAttributeString("r", "id", Rel, $"rId{i + 1}"); x.WriteEndElement();
            }
            x.WriteEndElement(); x.WriteEndElement();
        });
        Part("xl/_rels/workbook.xml.rels", x =>
        {
            x.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            for (int i = 0; i < workbook.Sheets.Count; i++) Relationship(x, $"rId{i + 1}", "worksheet", $"worksheets/sheet{i + 1}.xml");
            Relationship(x, "styles", "styles", "styles.xml"); x.WriteEndElement();
        });
        Part("xl/styles.xml", x =>
        {
            x.WriteStartElement("styleSheet", Main);
            x.WriteStartElement("numFmts", Main); x.WriteAttributeString("count", "1"); x.WriteStartElement("numFmt", Main); x.WriteAttributeString("numFmtId", "164"); x.WriteAttributeString("formatCode", "yyyy-mm-dd\"T\"hh:mm:ss"); x.WriteEndElement(); x.WriteEndElement();
            x.WriteStartElement("fonts", Main); x.WriteAttributeString("count", "2"); x.WriteStartElement("font", Main); x.WriteEndElement(); x.WriteStartElement("font", Main); x.WriteElementString("b", Main, ""); x.WriteEndElement(); x.WriteEndElement();
            x.WriteStartElement("fills", Main); x.WriteAttributeString("count", "2");
            foreach (string pattern in new[] { "none", "gray125" }) { x.WriteStartElement("fill", Main); x.WriteStartElement("patternFill", Main); x.WriteAttributeString("patternType", pattern); x.WriteEndElement(); x.WriteEndElement(); } x.WriteEndElement();
            x.WriteStartElement("borders", Main); x.WriteAttributeString("count", "1"); x.WriteStartElement("border", Main); x.WriteEndElement(); x.WriteEndElement();
            void Xf(int format, int font, bool cell)
            {
                x.WriteStartElement("xf", Main); x.WriteAttributeString("numFmtId", format.ToString(CultureInfo.InvariantCulture)); x.WriteAttributeString("fontId", font.ToString(CultureInfo.InvariantCulture)); x.WriteAttributeString("fillId", "0"); x.WriteAttributeString("borderId", "0");
                if (cell) { x.WriteAttributeString("xfId", "0"); x.WriteAttributeString("applyNumberFormat", "1"); x.WriteAttributeString("applyFont", "1"); } x.WriteEndElement();
            }
            x.WriteStartElement("cellStyleXfs", Main); x.WriteAttributeString("count", "1"); Xf(0, 0, false); x.WriteEndElement();
            x.WriteStartElement("cellXfs", Main); x.WriteAttributeString("count", "4"); Xf(0, 0, true); Xf(164, 0, true); Xf(0, 1, true); Xf(164, 1, true); x.WriteEndElement();
            x.WriteStartElement("cellStyles", Main); x.WriteAttributeString("count", "1"); x.WriteStartElement("cellStyle", Main); x.WriteAttributeString("name", "Normal"); x.WriteAttributeString("xfId", "0"); x.WriteAttributeString("builtinId", "0"); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        });
        for (int i = 0; i < workbook.Sheets.Count; i++)
        {
            var sheet = workbook.Sheets[i];
            Part($"xl/worksheets/sheet{i + 1}.xml", x =>
            {
                x.WriteStartElement("worksheet", Main);
                if (sheet.HasHeader)
                {
                    x.WriteStartElement("sheetViews", Main); x.WriteStartElement("sheetView", Main); x.WriteAttributeString("workbookViewId", "0");
                    x.WriteStartElement("pane", Main); x.WriteAttributeString("ySplit", "1"); x.WriteAttributeString("topLeftCell", "A2"); x.WriteAttributeString("activePane", "bottomLeft"); x.WriteAttributeString("state", "frozen"); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
                }
                x.WriteStartElement("sheetData", Main);
                for (int r = 0; r < sheet.Rows.Count; r++)
                {
                    ct.ThrowIfCancellationRequested();
                    x.WriteStartElement("row", Main); x.WriteAttributeString("r", (r + 1).ToString(CultureInfo.InvariantCulture));
                    for (int c = 0; c < sheet.Rows[r].Count; c++)
                    {
                        Cell cell = sheet.Rows[r][c];
                        x.WriteStartElement("c", Main); x.WriteAttributeString("r", Column(c) + (r + 1).ToString(CultureInfo.InvariantCulture));
                        bool header = sheet.HasHeader && r == 0;
                        x.WriteAttributeString("s", ((header ? 2 : 0) + (cell is DateCell ? 1 : 0)).ToString(CultureInfo.InvariantCulture));
                        if (cell is TextCell text)
                        {
                            x.WriteAttributeString("t", "inlineStr"); x.WriteStartElement("is", Main); x.WriteStartElement("t", Main); x.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve"); x.WriteString(text.Value); x.WriteEndElement(); x.WriteEndElement();
                        }
                        else if (cell is not EmptyCell)
                        {
                            string kind = cell switch { BooleanCell => "b", ErrorCell => "e", DateCell => "d", _ => "n" };
                            string value = cell switch
                            {
                                NumberCell n => n.Value.ToString("R", CultureInfo.InvariantCulture), BooleanCell b => b.Value ? "1" : "0",
                                DateCell d => XmlConvert.ToString(DateTime.SpecifyKind(d.Value, DateTimeKind.Unspecified), XmlDateTimeSerializationMode.Unspecified), ErrorCell e => e.Code,
                                _ => throw new XlsxException("Unsupported cell type.")
                            };
                            x.WriteAttributeString("t", kind); x.WriteElementString("v", Main, value);
                        }
                        x.WriteEndElement();
                    }
                    x.WriteEndElement();
                }
                x.WriteEndElement(); x.WriteEndElement();
            });
        }
    }
    private static string Column(int n)
    {
        string s = ""; for (n++; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s; return s;
    }
}
