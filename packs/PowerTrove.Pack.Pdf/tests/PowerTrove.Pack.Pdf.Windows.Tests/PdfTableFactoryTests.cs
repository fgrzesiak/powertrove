using FileKit.Pdf.Testing;
using FileKit.Xlsx;
using TextCell = FileKit.Xlsx.TextCell;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Infrastructure.Files;

namespace PowerTrove.Pack.Pdf.Windows.Tests;

public sealed class PdfTableFactoryTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    [Fact]
    public async Task DesktopFactoryExportsRealTableGeometryAcrossPagesToExcel()
    {
        string input = _directory.Write("tables.pdf", TestTablePdfFactory.Create(pages: 2, twoTables: true));
        var pack = new PdfPackFactory().Create(new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new FileSignatureVerifier())));
        var action = pack.Actions.Single(a => a.Descriptor.Id == "powertrove.pdf.to-xlsx"); var context = Snapshots.ForPaths(input);
        Assert.True((await action.MatchAsync(context, default)).IsAvailable); Assert.False((await action.PreviewAsync(context, new EmptyParameters(), default)).IsBlocked);
        var result = await action.ExecuteAsync(context, new EmptyParameters(), null, default); Assert.True(result.IsSuccess, result.Message);
        using var output = File.OpenRead(Assert.Single(result.OutputPaths)); var workbook = await XlsxReader.ReadAsync(output); Assert.Equal(4, workbook.Sheets.Count);
        Assert.Equal("00123", Assert.IsType<TextCell>(workbook.Sheets[0].Rows[1][1]).Value); Assert.False(workbook.HasFormulas);
    }
}
