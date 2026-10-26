using System.Text;
using System.Text.Json;

namespace FileKit.Tabular.Tests;

public sealed class TabularTests
{
    [Fact]
    public void QuotedFields_Newlines_Bom_AndDelimiterArePreserved()
    {
        var table = CsvReader.Parse("\uFEFFName;Note\r\nAda;\"a;\"\"b\"\"\r\nc\"\r\n");
        Assert.Equal(new[] { "Name", "Note" }, table.Headers);
        Assert.Equal("a;\"b\"\r\nc", Assert.IsType<TextCell>(Assert.Single(table.Rows)[1]).Value);
        using var writer = new StringWriter(); CsvWriter.Write(writer, table, ";");
        Assert.Equal("Name;Note\r\nAda;\"a;\"\"b\"\"\r\nc\"\r\n", writer.ToString());
    }
    [Theory]
    [InlineData("a,b\n1\n")]
    [InlineData("a,b\n\"x\"junk,2")]
    [InlineData("a,b\n\"unfinished,2")]
    [InlineData("a,b\nx\"y,2")]
    public void MalformedCsvIsTypedFailure(string text) => Assert.Throws<TabularException>(() => CsvReader.Parse(text));
    [Fact]
    public void LimitsApplyToRowsColumnsAndBytes()
    {
        Assert.Throws<TabularException>(() => CsvReader.Parse("a\nb\n", limits: new(MaxRows: 1)));
        Assert.Throws<TabularException>(() => CsvReader.Parse("a,b", limits: new(MaxColumns: 1)));
        Assert.Throws<TabularException>(() => CsvReader.Parse("ä", limits: new(MaxBytes: 1)));
        Assert.Throws<TabularException>(() => CsvReader.Parse("a,b\n1,2", limits: new(MaxCells: 3)));
    }
    [Fact]
    public async Task InvalidUtf8AndCancellationAreNotSilentlyAccepted()
    {
        using var bad = new MemoryStream(new byte[] { 0xFF });
        await Assert.ThrowsAsync<TabularException>(() => CsvReader.ReadAsync(bad));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CsvReader.ReadAsync(new MemoryStream("a,b"u8.ToArray()), cancellationToken: new CancellationToken(true)));
    }
    [Fact]
    public void HeaderResolutionMatchesEstablishedRules()
    {
        var table = CsvReader.Parse(",Name,name,Name_1\n1,2,3,4\n");
        Assert.Equal(new[] { "Column_1", "Name", "name_1", "Name_1_1" }, table.Headers);
    }
    [Fact]
    public void WritersRetainTypedAndEscapingConventions()
    {
        var table = new Table(new[] { "N", "Text", "Flag", "Empty" }, new[] { (IReadOnlyList<Cell>)new Cell[] { new NumberCell(2.5), new TextCell("O'Brien|x"), new BooleanCell(true), new EmptyCell() } });
        using var csv = new StringWriter(); CsvWriter.Write(csv, table); Assert.Equal("N,Text,Flag,Empty\r\n2.5,O'Brien|x,True,\r\n", csv.ToString());
        using var json = new MemoryStream(); JsonTableWriter.Write(json, table); using var parsed = JsonDocument.Parse(json.ToArray()); Assert.Equal(2.5, parsed.RootElement[0].GetProperty("N").GetDouble()); Assert.True(parsed.RootElement[0].GetProperty("Flag").GetBoolean());
        using var md = new StringWriter(); MarkdownTableWriter.Write(md, table); Assert.Contains("O'Brien&#124;x", md.ToString());
        using var sql = new StringWriter(); SqlInsertWriter.Write(sql, table, "T", "sqlserver"); Assert.Contains("N'O''Brien|x'", sql.ToString()); Assert.Contains("[N]", sql.ToString());
    }
    [Fact]
    public void MutatedCsvNeverEscapesAsAnUnexpectedParserException()
    {
        const string fixture = "a,b\r\n\"x,y\",z\r\n";
        for (int i = 0; i < fixture.Length; i++)
        {
            char[] mutation = fixture.ToCharArray(); mutation[i] = '"';
            try { CsvReader.Parse(new string(mutation), limits: new(MaxBytes: 1024, MaxRows: 10, MaxColumns: 10)); }
            catch (TabularException) { }
        }
    }
}
