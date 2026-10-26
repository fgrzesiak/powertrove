using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace FileKit.Xlsx;

/// <summary>Bounded OOXML reader. Formula expressions are never evaluated.</summary>
public static class XlsxReader
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static Task<Workbook> ReadAsync(Stream input, XlsxLimits? limits = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(input, limits ?? new(), cancellationToken), cancellationToken);

    private static Workbook Read(Stream input, XlsxLimits limits, CancellationToken ct)
    {
        try
        {
            if (!input.CanSeek || input.Length - input.Position > limits.MaxTotalBytes) throw Invalid();
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > limits.MaxEntries) throw Invalid();
            long total = 0;
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!SafePart(entry.FullName) || !entries.TryAdd(entry.FullName, entry) || entry.Length > limits.MaxPartBytes ||
                    entry.Length > Math.Max(1, entry.CompressedLength) * limits.MaxCompressionRatio ||
                    (total = checked(total + entry.Length)) > limits.MaxTotalBytes) throw Invalid();
            }
            XmlDocument Part(string path)
            {
                ct.ThrowIfCancellationRequested();
                if (!entries.TryGetValue(path, out var entry)) throw Invalid();
                using var stream = entry.Open();
                using var bounded = new BoundedReadStream(stream, limits.MaxPartBytes, ct);
                using var reader = XmlReader.Create(bounded, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = limits.MaxPartBytes, IgnoreComments = true });
                var doc = new XmlDocument { XmlResolver = null };
                doc.Load(reader);
                return doc;
            }
            var workbook = Part("xl/workbook.xml");
            var relationships = Part("xl/_rels/workbook.xml.rels");
            var sheetPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            string? stringsPath = null, stylesPath = null;
            foreach (XmlElement relationship in relationships.GetElementsByTagName("Relationship", "http://schemas.openxmlformats.org/package/2006/relationships"))
            {
                if (relationship.GetAttribute("TargetMode").Equals("External", StringComparison.OrdinalIgnoreCase)) continue;
                var target = ResolvePart(relationship.GetAttribute("Target"));
                string type = relationship.GetAttribute("Type");
                if (type == Rel + "/worksheet")
                {
                    if (!sheetPaths.TryAdd(relationship.GetAttribute("Id"), target)) throw Invalid();
                }
                else if (type == Rel + "/sharedStrings") stringsPath = target;
                else if (type == Rel + "/styles") stylesPath = target;
            }
            var strings = new List<string>();
            if (stringsPath is not null)
                foreach (XmlElement si in Part(stringsPath).GetElementsByTagName("si", Main))
                {
                    ct.ThrowIfCancellationRequested();
                    if (strings.Count >= limits.MaxCells) throw Invalid();
                    strings.Add(RichText(si));
                }
            var dateStyles = new List<bool>();
            if (stylesPath is not null)
            {
                var styles = Part(stylesPath);
                var formats = new Dictionary<int, string>();
                foreach (XmlElement format in styles.GetElementsByTagName("numFmt", Main))
                    formats.Add(Int(format, "numFmtId"), format.GetAttribute("formatCode"));
                var xfs = styles.GetElementsByTagName("cellXfs", Main);
                if (xfs.Count > 0)
                    foreach (XmlNode node in xfs[0]!.ChildNodes)
                        if (node is XmlElement xf && xf.LocalName == "xf")
                        {
                            int id = Int(xf, "numFmtId", 0);
                            dateStyles.Add(id is >= 14 and <= 22 || formats.TryGetValue(id, out var format) && IsDateFormat(format));
                        }
            }
            bool date1904 = false, formulas = false;
            var properties = workbook.GetElementsByTagName("workbookPr", Main);
            if (properties.Count > 0) date1904 = properties[0]!.Attributes?["date1904"]?.Value is "1" or "true";
            var sheets = new List<Worksheet>();
            int allocatedCells = 0;
            foreach (XmlElement sheet in workbook.GetElementsByTagName("sheet", Main))
            {
                ct.ThrowIfCancellationRequested();
                if (sheets.Count >= limits.MaxSheets || !sheetPaths.TryGetValue(sheet.GetAttribute("id", Rel), out var path)) throw Invalid();
                var xml = Part(path);
                var rows = new List<IReadOnlyList<Cell>>();
                int previousRow = 0;
                foreach (XmlElement row in xml.GetElementsByTagName("row", Main))
                {
                    ct.ThrowIfCancellationRequested();
                    int r = Int(row, "r", previousRow + 1);
                    if (r <= previousRow || r > limits.MaxRows) throw Invalid();
                    while (rows.Count < r - 1) rows.Add(Array.Empty<Cell>());
                    var cells = new List<Cell>();
                    foreach (XmlNode node in row.ChildNodes)
                    {
                        if (node is not XmlElement cell || cell.LocalName != "c") continue;
                        int col = cell.HasAttribute("r") ? Column(cell.GetAttribute("r"), r) : cells.Count;
                        if (col < cells.Count || col >= limits.MaxColumns || checked(allocatedCells + col + 1) > limits.MaxCells) throw Invalid();
                        while (cells.Count < col) cells.Add(new EmptyCell());
                        string text = Child(cell, "v")?.InnerText ?? "";
                        bool formula = Child(cell, "f") is not null;
                        formulas |= formula;
                        string kind = cell.GetAttribute("t");
                        Cell value;
                        if (kind == "inlineStr") value = new TextCell(Child(cell, "is") is { } inline ? RichText(inline) : "");
                        else if (kind == "s")
                        {
                            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= strings.Count) throw Invalid();
                            value = new TextCell(strings[index]);
                        }
                        else if (kind == "str") value = new TextCell(text);
                        else if (kind == "e") value = new ErrorCell(text);
                        else if (kind == "b") value = text switch { "1" => new BooleanCell(true), "0" => new BooleanCell(false), _ => throw Invalid() };
                        else if (kind == "d") value = new DateCell(XmlConvert.ToDateTime(text, XmlDateTimeSerializationMode.Unspecified));
                        else if (text.Length == 0) value = new EmptyCell();
                        else
                        {
                            if (kind.Length != 0 && kind != "n" || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) throw Invalid();
                            int style = Int(cell, "s", 0);
                            if (style < 0 || (style > 0 && style >= dateStyles.Count)) throw Invalid();
                            value = style < dateStyles.Count && dateStyles[style]
                                ? ExcelDate(number, date1904) : new NumberCell(number);
                        }
                        cells.Add(value);
                    }
                    allocatedCells = checked(allocatedCells + cells.Count);
                    rows.Add(cells); previousRow = r;
                }
                // Merged ranges have a value only at their anchor, even in malformed producers.
                long mergeWork = 0;
                foreach (XmlElement merge in xml.GetElementsByTagName("mergeCell", Main))
                {
                    string[] range = merge.GetAttribute("ref").Split(':');
                    if (range.Length != 2) throw Invalid();
                    var (r1, c1) = Address(range[0]); var (r2, c2) = Address(range[1]);
                    if (r1 > r2 || c1 > c2 || r2 > limits.MaxRows || c2 >= limits.MaxColumns) throw Invalid();
                    for (int r = r1; r <= Math.Min(r2, rows.Count); r++)
                    {
                        ct.ThrowIfCancellationRequested();
                        mergeWork += 1L + Math.Max(0, Math.Min(c2 + 1, rows[r - 1].Count) - c1);
                        if (mergeWork > (long)limits.MaxCells + limits.MaxRows) throw Invalid();
                        for (int c = c1; c <= Math.Min(c2, rows[r - 1].Count - 1); c++)
                            if (r != r1 || c != c1) ((List<Cell>)rows[r - 1])[c] = new EmptyCell();
                    }
                }
                string name = sheet.GetAttribute("name");
                if (string.IsNullOrWhiteSpace(name) || sheets.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw Invalid();
                sheets.Add(new Worksheet(name, rows));
            }
            if (sheets.Count == 0) throw Invalid();
            return new Workbook(sheets, formulas);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or FormatException or OverflowException or ArgumentException)
        { throw new XlsxException("The workbook is invalid or exceeds the supported limits.", ex); }
    }
    private static Cell ExcelDate(double value, bool date1904)
    {
        if (!date1904 && value is >= 60 and < 61) return new ErrorCell("#VALUE!"); // Excel's fictitious 1900-02-29.
        return new DateCell(date1904 ? new DateTime(1904, 1, 1).AddDays(value) : DateTime.FromOADate(value < 60 ? value + 1 : value));
    }
    internal static bool IsDateFormat(string format)
    {
        var visible = new StringBuilder(); bool quoted = false, bracket = false;
        for (int i = 0; i < format.Length; i++)
        {
            char c = char.ToLowerInvariant(format[i]);
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c is '\\' or '_' or '*') { i++; continue; }
            if (c == '[') { bracket = true; continue; }
            if (c == ']') { bracket = false; continue; }
            if (!bracket) visible.Append(c);
        }
        string code = visible.ToString();
        return code.Contains('y') || code.Contains('d') || code.Contains('m') && !code.Contains('h') && !code.Contains('s');
    }
    private static string RichText(XmlElement parent) => string.Concat(parent.GetElementsByTagName("t", Main).Cast<XmlElement>().Where(t => t.ParentNode?.LocalName != "rPh").Select(t => t.InnerText));
    private static XmlElement? Child(XmlElement e, string name) => e.ChildNodes.OfType<XmlElement>().FirstOrDefault(c => c.LocalName == name && c.NamespaceURI == Main);
    private static int Int(XmlElement e, string attr, int? fallback = null) => e.HasAttribute(attr) ? int.Parse(e.GetAttribute(attr), CultureInfo.InvariantCulture) : fallback ?? throw Invalid();
    private static (int Row, int Col) Address(string reference)
    {
        int n = 0; while (n < reference.Length && char.IsAsciiLetter(reference[n])) n++;
        if (n == 0 || !int.TryParse(reference.AsSpan(n), NumberStyles.None, CultureInfo.InvariantCulture, out int row) || row < 1) throw Invalid();
        return (row, Column(reference, row));
    }
    private static int Column(string reference, int row)
    {
        int col = 0, i = 0;
        while (i < reference.Length && reference[i] is >= 'A' and <= 'Z') col = checked(col * 26 + reference[i++] - 'A' + 1);
        if (i == 0 || !int.TryParse(reference.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out int r) || row != r) throw Invalid();
        return col - 1;
    }
    private static bool SafePart(string name) => name.Length > 0 && !name.StartsWith('/') && !name.Contains('\\') && !name.Contains(':') && !name.Split('/').Any(p => p is "." or "..");
    private static string ResolvePart(string target)
    {
        if (target.Contains('\\') || target.Contains(':') || target.Contains('%') || target.Contains('#') || target.Contains('?')) throw Invalid();
        string path = target.StartsWith('/') ? target[1..] : "xl/" + target;
        var parts = new List<string>();
        foreach (string segment in path.Split('/'))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (parts.Count == 0) throw Invalid(); parts.RemoveAt(parts.Count - 1); }
            else if (segment.Length > 0) parts.Add(segment);
        }
        path = string.Join('/', parts);
        if (!path.StartsWith("xl/", StringComparison.Ordinal) || !SafePart(path)) throw Invalid();
        return path;
    }
    private static XlsxException Invalid() => new("The workbook is invalid or exceeds the supported limits.");
    private sealed class BoundedReadStream(Stream inner, long limit, CancellationToken ct) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) { ct.ThrowIfCancellationRequested(); int n = inner.Read(buffer); if ((_read += n) > limit) throw Invalid(); return n; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
