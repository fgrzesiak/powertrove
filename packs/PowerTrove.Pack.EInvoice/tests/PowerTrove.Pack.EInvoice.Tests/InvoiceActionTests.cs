using System.Text;
using FileKit.EInvoice.Testing;
using FileKit.Html;
using FileKit.Pdf.Tests;
using FileKit.Xlsx;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Pdf.Html;
using PowerTrove.Pack.Pdf.Windows;
using Windows.Data.Pdf;
using Windows.Storage;
using TextCell = FileKit.Xlsx.TextCell;

namespace PowerTrove.Pack.EInvoice.Tests;

public sealed class InvoiceActionTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    private static EInvoicePack Pack() => new(FileActionServices.CreatePhysical(new FileSignatureVerifier()));
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredViewsAndExportsPreserveInvoiceValuesEncodeHtmlAndKeepOriginals(bool cii)
    {
        byte[] xml = cii ? TestInvoiceFactory.Cii() : TestInvoiceFactory.Ubl(description: "=2+2<script>active()</script>");
        string one = _directory.Write("one.xml", xml), two = _directory.Write("two.xml", xml); _directory.Write("one-invoice.html", "existing");
        foreach (string id in new[] { "view", "to-csv", "to-xlsx" })
        {
            var action = Pack().Actions.Single(a => a.Descriptor.Id == "powertrove.einvoice." + id); var context = Snapshots.ForPaths(one, two);
            Assert.True((await action.MatchAsync(context, default)).IsAvailable); var preview = await action.PreviewAsync(context, new EmptyParameters(), default); Assert.False(preview.IsBlocked, preview.PreviewText); Assert.DoesNotContain("000123", preview.PreviewText);
            var result = await action.ExecuteAsync(context, new EmptyParameters(), null, default); Assert.True(result.IsSuccess, result.Message); Assert.Equal(2, result.OutputPaths.Count);
            string output = result.OutputPaths[0];
            if (id == "view") { string html = File.ReadAllText(output); Assert.Contains("000123", html); Assert.DoesNotContain("<script>", html); Assert.Contains("119", html); Assert.Contains("99", html); Assert.Contains("Content-Security-Policy", html); }
            if (id == "to-csv") { string csv = File.ReadAllText(output); Assert.Contains("000123", csv); Assert.Contains("001", csv); if (!cii) Assert.Contains("'=2+2", csv); }
            if (id == "to-xlsx")
            {
                using var input = File.OpenRead(output); var workbook = await XlsxReader.ReadAsync(input); Assert.Equal(4, workbook.Sheets.Count); Assert.False(workbook.HasFormulas);
                Assert.Equal("000123", Assert.IsType<TextCell>(workbook.Sheets[1].Rows[1][0]).Value); Assert.Equal("001", Assert.IsType<TextCell>(workbook.Sheets[1].Rows[1][6]).Value);
            }
        }
        Assert.Equal(xml, File.ReadAllBytes(one)); Assert.Equal("existing", File.ReadAllText(_directory.File("one-invoice.html")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FactoryWithRealHostPrinterCreatesAnActualPdfVisualization(bool cii)
    {
        string input = _directory.Write("invoice.xml", cii ? TestInvoiceFactory.Cii() : TestInvoiceFactory.Ubl());
        var host = new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new FileSignatureVerifier())).Add<ILocalHtmlPdfPrinter>(new WindowsHtmlPdfRenderer(new SafeHtmlMarkupValidator()));
        var action = new EInvoicePackFactory().Create(host).Actions.Single(a => a.Descriptor.Id == "powertrove.einvoice.to-pdf");
        var result = await action.ExecuteAsync(Snapshots.ForPaths(input), Snapshots.Parameters(("Paper", "letter"), ("MarginMm", "12")), null, default); Assert.True(result.IsSuccess, result.Message);
        var pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(Assert.Single(result.OutputPaths))); Assert.True(pdf.PageCount > 0);
        using var page = pdf.GetPage(0); Assert.InRange(page.Size.Width, 814, 818);
    }
    [Fact]
    public async Task FactoryWithoutAHostPrinterPrintsThroughThePacksOwnPrinterLikeAWorker()
    {
        string input = _directory.Write("worker-invoice.xml", TestInvoiceFactory.Ubl());
        var host = new PackHostServices(new Version(0, 2, 0), FileActionServices.CreatePhysical(new FileSignatureVerifier()));
        var action = new EInvoicePackFactory().Create(host).Actions.Single(a => a.Descriptor.Id == "powertrove.einvoice.to-pdf");
        var result = await action.ExecuteAsync(Snapshots.ForPaths(input), Snapshots.Parameters(("Paper", "a4")), null, default); Assert.True(result.IsSuccess, result.Message);
        var pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(Assert.Single(result.OutputPaths))); Assert.True(pdf.PageCount > 0);
    }
    [Theory]
    [InlineData("<html><body><script>x()</script></body></html>")]
    [InlineData("<html><body><p onclick='x()'>a</p></body></html>")]
    [InlineData("<html><head><meta http-equiv='refresh' content='0;url=https://example.invalid'></head><body></body></html>")]
    [InlineData("<html><head><link rel='stylesheet' href='https://example.invalid/a.css'></head><body></body></html>")]
    public void PacksOwnPrinterRejectsActiveOrNavigatingMarkup(string html) =>
        Assert.Throws<HtmlPrintException>(() => new PowerTrove.Pack.EInvoice.Html.PassiveHtmlMarkupValidator().ValidateAndNormalize(html));
    [Fact]
    public async Task EmbeddedInvoiceExtractionAndViewingNeverUseAttachmentNamesAsFilesystemPaths()
    {
        byte[] xml = TestInvoiceFactory.Cii(); string input = _directory.Write("hybrid.pdf", Embedded(xml, "factur-x.xml"));
        foreach (string id in new[] { "extract-xml", "view-embedded" })
        {
            var action = Pack().Actions.Single(a => a.Descriptor.Id == "powertrove.einvoice." + id); var context = Snapshots.ForPaths(input);
            Assert.False((await action.PreviewAsync(context, new EmptyParameters(), default)).IsBlocked);
            var result = await action.ExecuteAsync(context, new EmptyParameters(), null, default); Assert.True(result.IsSuccess, result.Message);
            if (id == "extract-xml") Assert.Equal(xml, File.ReadAllBytes(Assert.Single(result.OutputPaths)));
            else Assert.Contains("000123", File.ReadAllText(Assert.Single(result.OutputPaths)));
        }
        string unsafeInput = _directory.Write("unrecognized.pdf", Embedded(xml, "../../factur-x.xml"));
        var extract = Pack().Actions.Single(a => a.Descriptor.Id == "powertrove.einvoice.extract-xml");
        Assert.True((await extract.PreviewAsync(Snapshots.ForPaths(unsafeInput), new EmptyParameters(), default)).IsBlocked);
        Assert.False((await extract.ExecuteAsync(Snapshots.ForPaths(unsafeInput), new EmptyParameters(), null, default)).IsSuccess);
        Assert.False(File.Exists(_directory.File("unrecognized-invoice.xml")));
    }
    [Fact]
    public async Task UnsupportedXmlAndMissingPrinterFailWithoutCreatingResults()
    {
        string input = _directory.Write("ordinary.xml", "<ordinary/>"); var context = Snapshots.ForPaths(input);
        var view = Pack().Actions.Single(a => a.Descriptor.Id == "powertrove.einvoice.view"); Assert.True((await view.PreviewAsync(context, new EmptyParameters(), default)).IsBlocked);
        Assert.False((await view.ExecuteAsync(context, new EmptyParameters(), null, default)).IsSuccess); Assert.Empty(_directory.NewFiles(input));
        var pdf = Pack().Actions.Single(a => a.Descriptor.Id == "powertrove.einvoice.to-pdf"); Assert.False((await pdf.MatchAsync(context, default)).IsAvailable);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.PreviewAsync(context, new EmptyParameters(), new(true)).AsTask());
    }
    [Fact]
    public async Task CyclicEmbeddedNameTreesAreRefusedWithoutOutput()
    {
        byte[] cyclic = TestPdfFactory.Serialize(new[] {
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 5 0 R >> >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 595 842] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>", TestPdfFactory.StreamObject("", ""), "<< /Kids [5 0 R] >>"
        }, "/Root 1 0 R");
        string input = _directory.Write("cycle.pdf", cyclic);
        var action = Pack().Actions.Single(a => a.Descriptor.Id == "powertrove.einvoice.extract-xml");
        Assert.True((await action.PreviewAsync(Snapshots.ForPaths(input), new EmptyParameters(), default)).IsBlocked);
        var result = await action.ExecuteAsync(Snapshots.ForPaths(input), new EmptyParameters(), null, default); Assert.False(result.IsSuccess); Assert.Empty(result.OutputPaths); Assert.Empty(_directory.NewFiles(input));
    }
    private static byte[] Embedded(byte[] xml, string name)
    {
        string escaped = name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
        return TestPdfFactory.Serialize(new[] {
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(invoice) 5 0 R] >> >> /AF [5 0 R] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 595 842] >>", "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>", TestPdfFactory.StreamObject("", ""),
            "<< /Type /Filespec /F (" + escaped + ") /AFRelationship /Data /EF << /F 6 0 R >> >>",
            TestPdfFactory.StreamObject("/Type /EmbeddedFile /Subtype /application#2Fxml", Encoding.Latin1.GetString(xml), compress: true)
        }, "/Root 1 0 R");
    }
}
