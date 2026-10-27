using FileKit.Pdf;
using FileKit.Pdf.Testing;
using FileKit.Tabular;
using FileKit.Xlsx;
using TextCell = FileKit.Xlsx.TextCell;
using PowerTrove.Pack.Pdf.Layout;

namespace PowerTrove.Pack.Pdf.Tests;

public sealed class PdfToExcelTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealPdfLayoutAndRegisteredBatchActionProduceStructuredWorksheetsWithTextIdentifiers(bool ruled)
    {
        byte[] source = TestTablePdfFactory.Create(ruled, pages: 2, twoTables: true);
        string one = _directory.Write("one.pdf", source), two = _directory.Write("two.pdf", source); _directory.Write("one.xlsx", "existing");
        var action = new PdfPack(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()), positionedReader: new PdfPositionedReader()).Actions.Single(a => a.Descriptor.Id == "powertrove.pdf.to-xlsx");
        var context = Snapshots.ForPaths(one, two); var preview = await action.PreviewAsync(context, new EmptyParameters(), default); Assert.False(preview.IsBlocked, preview.PreviewText); Assert.DoesNotContain("Coffee", preview.PreviewText);
        var result = await action.ExecuteAsync(context, new EmptyParameters(), null, default); Assert.True(result.IsSuccess, result.Message); Assert.Equal(2, result.OutputPaths.Count);
        using var xlsx = File.OpenRead(result.OutputPaths[0]); var book = await XlsxReader.ReadAsync(xlsx); Assert.Equal(4, book.Sheets.Count); Assert.False(book.HasFormulas);
        foreach (var sheet in book.Sheets)
        {
            Assert.Equal(3, sheet.Rows.Count); Assert.All(sheet.Rows, row => Assert.Equal(3, row.Count));
            Assert.Equal("Item", Assert.IsType<TextCell>(sheet.Rows[0][0]).Value); Assert.Equal("Coffee beans", Assert.IsType<TextCell>(sheet.Rows[1][0]).Value);
            Assert.Equal("00123", Assert.IsType<TextCell>(sheet.Rows[1][1]).Value); Assert.Equal("=2+2", Assert.IsType<TextCell>(sheet.Rows[2][1]).Value);
        }
        Assert.Equal(source, File.ReadAllBytes(one)); Assert.Equal("existing", File.ReadAllText(_directory.File("one.xlsx")));
    }
    [Fact]
    public async Task UnclearStructuresParagraphOnlyAndDisabledInferenceNeverPublishFallbackText()
    {
        var action = new PdfToExcelAction(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()), new PdfPositionedReader());
        foreach (var source in new[] { TestTablePdfFactory.Create(false, ambiguous: true), TestTablePdfFactory.Create(noTable: true) })
        {
            string input = _directory.Write(Guid.NewGuid() + ".pdf", source); var context = Snapshots.ForPaths(input);
            Assert.True((await action.PreviewAsync(context, new EmptyParameters(), default)).IsBlocked);
            var result = await action.ExecuteAsync(context, new EmptyParameters(), null, default); Assert.False(result.IsSuccess); Assert.Empty(result.OutputPaths);
        }
        string unruled = _directory.Write("unruled.pdf", TestTablePdfFactory.Create(false));
        Assert.True((await action.PreviewAsync(Snapshots.ForPaths(unruled), Snapshots.Parameters(("InferUnruled", "false")), default)).IsBlocked);
        using var cancelled = new MemoryStream(TestTablePdfFactory.Create()); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PdfPositionedReader().ReadAsync(cancelled, new(true)));
    }
    [Fact]
    public void StructuralDecompressionIsBoundedBeforeExpansionCompletes()
    {
        byte[] compressed = FileKit.Pdf.Tests.TestPdfFactory.Zlib(new byte[2 * 1024 * 1024]);
        var stream = new PdfStream(new PdfDictionary { ["Filter"] = new PdfName("FlateDecode") }, compressed);
        Assert.Throws<PdfLimitExceededException>(() => PdfStreamDecoder.Decode(stream, 1024));
        Assert.Equal(2 * 1024 * 1024, PdfStreamDecoder.Decode(stream, 3 * 1024 * 1024).Length);
        stream.Dictionary["DecodeParms"] = new PdfDictionary { ["Predictor"] = new PdfInteger(12), ["Columns"] = new PdfInteger(1048576), ["Colors"] = new PdfInteger(32), ["BitsPerComponent"] = new PdfInteger(16) };
        Assert.Throws<PdfLimitExceededException>(() => PdfStreamDecoder.Decode(stream, 1024));
    }
}
