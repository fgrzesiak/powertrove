using System.IO.Compression;
using System.Text;

namespace FileKit.Xlsx.Tests;

public sealed class XlsxTests
{
    [Fact]
    public async Task RoundTripPreservesTypedAndEmptyCellsAcrossSheets()
    {
        IReadOnlyList<Cell> row = [new TextCell(" =literal <ä> "), new NumberCell(1.25), new BooleanCell(true), new DateCell(new DateTime(2025, 3, 9, 12, 30, 1)), new EmptyCell(), new ErrorCell("#DIV/0!")];
        var expected = new Workbook([new("First", [row], true), new("Second", [[], [new TextCell("last")]])]);
        using var stream = new MemoryStream(); await XlsxWriter.WriteAsync(stream, expected); stream.Position = 0;
        var actual = await XlsxReader.ReadAsync(stream);
        Assert.Equal(2, actual.Sheets.Count); Assert.Equal(row, actual.Sheets[0].Rows[0]);
        Assert.Empty(actual.Sheets[1].Rows[0]); Assert.Equal(new TextCell("last"), actual.Sheets[1].Rows[1][0]);
        using var zip = new ZipArchive(new MemoryStream(stream.ToArray()), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        string xml = await reader.ReadToEndAsync(); Assert.Contains("state=\"frozen\"", xml); Assert.Contains("inlineStr", xml);
    }
    [Fact]
    public async Task ReadsSharedRichStringsCachedFormula1904CustomDatesAndMergedAnchors()
    {
        var parts = await Parts();
        parts["xl/workbook.xml"] = parts["xl/workbook.xml"].Replace("<sheets>", "<workbookPr date1904=\"1\"/><sheets>");
        parts["xl/_rels/workbook.xml.rels"] = parts["xl/_rels/workbook.xml.rels"].Replace("</Relationships>", "<Relationship Id=\"shared\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>");
        parts["xl/sharedStrings.xml"] = "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><r><t>A</t></r><r><t>B</t></r></si></sst>";
        parts["xl/worksheets/sheet1.xml"] = Sheet("<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" s=\"1\"><v>1.5</v></c><c r=\"C1\"><f>1+2</f><v>3</v></c><c r=\"D1\" t=\"e\"><v>#N/A</v></c></row>", "<mergeCells><mergeCell ref=\"C1:D1\"/></mergeCells>");
        using var bytes = Zip(parts); var book = await XlsxReader.ReadAsync(bytes);
        Assert.True(book.HasFormulas); Assert.Equal(new TextCell("AB"), book.Sheets[0].Rows[0][0]);
        Assert.Equal(new DateCell(new DateTime(1904, 1, 2, 12, 0, 0)), book.Sheets[0].Rows[0][1]);
        Assert.Equal(new NumberCell(3), book.Sheets[0].Rows[0][2]); Assert.IsType<EmptyCell>(book.Sheets[0].Rows[0][3]);
    }
    [Theory]
    [InlineData(14, "", 1, 1900, 1, 1)]
    [InlineData(22, "", 61, 1900, 3, 1)]
    [InlineData(165, "dd/mm/yyyy", 45292, 2024, 1, 1)]
    public async Task DetectsDateFormatsAnd1900LeapYearBoundary(int id, string format, double value, int year, int month, int day)
    {
        var parts = await Parts();
        parts["xl/styles.xml"] = parts["xl/styles.xml"].Replace("164", id.ToString()).Replace("yyyy-mm-dd&quot;T&quot;hh:mm:ss", format);
        parts["xl/worksheets/sheet1.xml"] = Sheet($"<row r=\"1\"><c r=\"A1\" s=\"1\"><v>{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c></row>");
        using var stream = Zip(parts); var book = await XlsxReader.ReadAsync(stream);
        Assert.Equal(new DateCell(new DateTime(year, month, day)), book.Sheets[0].Rows[0][0]);
    }
    [Theory]
    [InlineData("<!DOCTYPE worksheet [<!ENTITY a 'boom'>]><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>&a;</sheetData></worksheet>")]
    [InlineData("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1048577\"/></sheetData></worksheet>")]
    [InlineData("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"XFE1\"><v>1</v></c></row></sheetData></worksheet>")]
    [InlineData("<worksheet>")]
    public async Task MutatedXmlIsRejected(string xml)
    {
        var parts = await Parts(); parts["xl/worksheets/sheet1.xml"] = xml;
        using var stream = Zip(parts); await Assert.ThrowsAsync<XlsxException>(() => XlsxReader.ReadAsync(stream));
    }
    [Fact]
    public async Task PartSizeAndCompressionBombLimitsRejectGeneratedInputs()
    {
        var parts = await Parts(); parts["xl/worksheets/sheet1.xml"] = Sheet("<!--" + new string('x', 20000) + "-->");
        using var a = Zip(parts); await Assert.ThrowsAsync<XlsxException>(() => XlsxReader.ReadAsync(a, new(MaxPartBytes: 10000)));
        using var b = Zip(parts); await Assert.ThrowsAsync<XlsxException>(() => XlsxReader.ReadAsync(b, new(MaxCompressionRatio: 2)));
    }
    [Fact]
    public async Task RejectsTraversalExternalSheetAndMissingParts()
    {
        foreach (int mutation in Enumerable.Range(0, 3))
        {
            var parts = await Parts();
            if (mutation == 0) parts["../outside.xml"] = "<root/>";
            if (mutation == 1) parts["xl/_rels/workbook.xml.rels"] = parts["xl/_rels/workbook.xml.rels"].Replace("Target=\"worksheets/sheet1.xml\"", "TargetMode=\"External\" Target=\"https://example.invalid/file.xml\"");
            if (mutation == 2) parts.Remove("xl/worksheets/sheet1.xml");
            using var stream = Zip(parts); await Assert.ThrowsAsync<XlsxException>(() => XlsxReader.ReadAsync(stream));
        }
    }
    [Fact]
    public void SheetNamesAreSanitizedTruncatedAndUniqueIgnoringCase()
    {
        var used = new HashSet<string>(); string a = SheetNames.Unique("'a/b:*?[]'", used), b = SheetNames.Unique(a.ToUpperInvariant(), used);
        Assert.DoesNotContain('/', a); Assert.NotEqual(a.ToUpperInvariant(), b.ToUpperInvariant());
        string longName = SheetNames.Unique(new string('a', 40), used); Assert.Equal(31, longName.Length);
        Assert.EndsWith(" (2)", SheetNames.Unique(new string('a', 40), used));
    }
    private static string Sheet(string rows, string tail = "") => "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" + rows + "</sheetData>" + tail + "</worksheet>";
    private static async Task<Dictionary<string, string>> Parts()
    {
        using var stream = new MemoryStream(); await XlsxWriter.WriteAsync(stream, new([new("Sheet", [[new TextCell("x")]], true)]));
        stream.Position = 0; using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); });
    }
    private static MemoryStream Zip(Dictionary<string, string> parts)
    {
        var stream = new MemoryStream(); using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, xml) in parts) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(xml); }
        stream.Position = 0; return stream;
    }
}
