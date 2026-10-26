using X = FileKit.Xlsx;

namespace PowerTrove.Pack.Tables.Tests;

public sealed class CombineWorkbookTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();
    [Fact]
    public async Task TwoCsvAndTwoSheetWorkbookYieldFourTypedSheets()
    {
        string a = _dir.Write("one.csv", "key,value\r\na,1\r\n"), b = _dir.Write("two.tsv", "key\tvalue\r\nb\t2\r\n"), x = _dir.File("three.xlsx");
        using (var stream = File.Create(x)) await X.XlsxWriter.WriteAsync(stream, new([new("Alpha", [[new X.BooleanCell(true)]]), new("Beta", [[new X.NumberCell(3)]])]));
        var services = FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
        var action = new CombineWorkbookAction(services); var snapshot = Snapshots.ForPaths(a, b, x);
        Assert.Equal(SelectionFit.Ideal, SelectionRequirements.Evaluate(action.Descriptor.Selection!, snapshot));
        var result = await action.ExecuteAsync(snapshot, new EmptyParameters(), null, default);
        Assert.True(result.IsSuccess); using var read = File.OpenRead(Assert.Single(result.OutputPaths)); var book = await X.XlsxReader.ReadAsync(read);
        Assert.Equal(4, book.Sheets.Count); Assert.Equal(new[] { "one", "three - Alpha", "three - Beta", "two" }, book.Sheets.Select(s => s.Name));
        Assert.Equal(new X.TextCell("a"), book.Sheets[0].Rows[1][0]); Assert.Equal(new X.BooleanCell(true), book.Sheets[1].Rows[0][0]); Assert.Equal(new X.NumberCell(3), book.Sheets[2].Rows[0][0]);
    }
    [Fact]
    public async Task UnsupportedSpreadsheetIsNotSilentlyDropped()
    {
        var action = new CombineWorkbookAction(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()));
        var snapshot = Snapshots.ForPaths(_dir.Write("a.csv", "a\n"), _dir.Write("b.csv", "b\n"), _dir.Write("c.xls", "legacy"));
        Assert.False((await action.MatchAsync(snapshot, default)).IsAvailable);
        Assert.True((await action.PreviewAsync(snapshot, new EmptyParameters(), default)).IsBlocked);
    }
}
