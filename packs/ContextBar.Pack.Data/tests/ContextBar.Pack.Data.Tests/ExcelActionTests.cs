using System.Text.Json;
using X = FileKit.Xlsx;

namespace ContextBar.Pack.Data.Tests;

public sealed class ExcelActionTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();
    private static FileActionServices Services() => FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
    [Fact]
    public async Task CsvRoundTripPreservesAllRowsAndQuotedValues()
    {
        string csv = _dir.Write("input.csv", "name,value\r\n\"a,b\",01\r\nempty,\r\n");
        var result = await new ExcelAction(Services(), "csv-to-xlsx").ExecuteAsync(Snapshots.ForPaths(csv), new EmptyParameters(), null, default);
        Assert.True(result.IsSuccess); string xlsx = Assert.Single(result.OutputPaths);
        using var stream = File.OpenRead(xlsx); var book = await X.XlsxReader.ReadAsync(stream);
        Assert.Equal(3, book.Sheets[0].Rows.Count); Assert.Equal(new X.TextCell("01"), book.Sheets[0].Rows[1][1]);
        var back = await new ExcelAction(Services(), "xlsx-to-csv").ExecuteAsync(Snapshots.ForPaths(xlsx), new EmptyParameters(), null, default);
        Assert.True(back.IsSuccess); Assert.Equal(File.ReadAllText(csv), File.ReadAllText(Assert.Single(back.OutputPaths)));
    }
    [Theory]
    [InlineData("xlsx-to-csv")][InlineData("xlsx-to-json")]
    public async Task AllSheetsHaveSanitizedOutputsAndInvariantDatesAndNumbers(string mode)
    {
        string source = _dir.File("workbook.xlsx");
        using (var stream = File.Create(source))
            await X.XlsxWriter.WriteAsync(stream, new([new("Sheet A", [[new X.DateCell(new DateTime(2024, 1, 2, 3, 4, 5)), new X.NumberCell(1.25)]]), new("Sheet <B>|", [[new X.TextCell("text")]])]));
        var action = new ExcelAction(Services(), mode); var snapshot = Snapshots.ForPaths(source); var options = Snapshots.Parameters(("Sheet", "all"));
        var preview = await action.PreviewAsync(snapshot, options, default); Assert.Equal(2, preview.ExpectedOutputs.Count); Assert.NotEmpty(preview.Warnings);
        var result = await action.ExecuteAsync(snapshot, options, null, default);
        Assert.True(result.IsSuccess); Assert.Equal(2, result.OutputPaths.Count);
        Assert.All(result.OutputPaths, p => Assert.DoesNotContain(Path.GetFileName(p), c => "<>:\"/\\|?*".Contains(c)));
        string first = result.OutputPaths.Single(p => Path.GetFileName(p).Contains("Sheet A")); string content = File.ReadAllText(first);
        Assert.Contains("2024-01-02T03:04:05", content); Assert.Contains("1.25", content); Assert.DoesNotContain(source, result.Message!);
        if (mode == "xlsx-to-json") { using var json = JsonDocument.Parse(content); Assert.Equal(1.25, json.RootElement[0][1].GetDouble()); }
    }
    [Fact]
    public async Task DamagedWorkbookFailsWithoutPublishingOutput()
    {
        string source = _dir.Write("damaged.xlsx", "not a workbook");
        var receipt = await new ExcelAction(Services(), "xlsx-to-csv").ExecuteAsync(Snapshots.ForPaths(source), new EmptyParameters(), null, default);
        Assert.False(receipt.IsSuccess); Assert.Empty(receipt.OutputPaths); Assert.Empty(_dir.NewFiles(source));
    }
}
