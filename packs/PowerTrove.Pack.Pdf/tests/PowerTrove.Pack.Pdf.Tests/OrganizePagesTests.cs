using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Pdf;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf.Tests;

public sealed class OrganizePagesTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IAction Action() =>
        new PdfPack(FileActionServices.CreatePhysical(new FileSignatureVerifier())).Actions
            .Single(a => a.Descriptor.Id == OrganizePagesPdfAction.ActionId);

    private string Fixture(string name) =>
        _dir.Write(name, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "linked-objstm.pdf")));

    private static DictionaryParameters Pages(string value) =>
        new(new Dictionary<string, string> { ["Pages"] = value });

    [Fact]
    public void Arrangement_RoundTripsThroughTextForm()
    {
        var pages = new[] { new ArrangedPage(0, 2, 0), new ArrangedPage(1, 0, 90), new ArrangedPage(0, 0, 270) };

        string text = PageArrangement.Format(pages);

        Assert.Equal("1:3, 2:1@90, 1:1@270", text);
        Assert.True(PageArrangement.TryParse(text, out var parsed, out _));
        Assert.Equal(pages, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("0:1")]
    [InlineData("1:0")]
    [InlineData("1:-2")]
    [InlineData("1:2@45")]
    [InlineData("1:2@x")]
    [InlineData("a:b")]
    [InlineData("1:99999999999")]
    public void Arrangement_RejectsInvalidText(string text)
    {
        Assert.False(PageArrangement.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Arrangement_NormalizesRotationAndBoundsLength()
    {
        Assert.True(PageArrangement.TryParse("1:1@-90, 1:1@450", out var pages, out _));
        Assert.Equal(new[] { 270, 90 }, pages.Select(p => p.Rotation));

        string tooMany = string.Join(",", Enumerable.Repeat("1:1", PageArrangement.MaxPages + 1));
        Assert.False(PageArrangement.TryParse(tooMany, out _, out _));
    }

    [Fact]
    public async Task Execute_ReordersRotatesAndDropsPagesOfOnePdf()
    {
        string input = Fixture("report.pdf");

        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(input), Pages("1:3, 1:1@90"), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);
        string output = Assert.Single(receipt.OutputPaths);
        Assert.Equal(_dir.File("report (organized).pdf"), output);
        var result = PdfDocument.Open(output);
        var source = PdfDocument.Open(input);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(PageArrangement.Normalize(source.Pages[0].Rotate + 90), result.Pages[1].Rotate);
        Assert.False(result.WasRepaired);
    }

    [Fact]
    public async Task Execute_CombinesPagesAcrossPdfsInNaturalNameOrder()
    {
        string second = Fixture("part10.pdf");
        string first = Fixture("part2.pdf");

        // File 1 is part2 (natural order), file 2 is part10, regardless of selection order.
        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(second, first), Pages("2:1, 1:1, 2:3"), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);
        Assert.Equal(_dir.File("part2 (organized).pdf"), Assert.Single(receipt.OutputPaths));
        Assert.Equal(3, PdfDocument.Open(receipt.OutputPaths[0]).PageCount);
        var selected = ((FileSelection)Snapshots.ForPaths(second, first).Content).Files;
        Assert.Equal(new[] { "part2", "part10" }, OrganizePagesPdfAction.SourceOrder(selected).Select(f => Path.GetFileNameWithoutExtension(f.Name)));
    }

    [Theory]
    [InlineData("1:9")]
    [InlineData("3:1")]
    public async Task Execute_UnknownPageOrFile_FailsWithoutOutput(string pages)
    {
        string input = Fixture("report.pdf");

        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(input), Pages(pages), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Empty(_dir.NewFiles(input));
    }

    [Fact]
    public async Task Preview_InvalidArrangement_IsBlocked()
    {
        string input = Fixture("report.pdf");

        var preview = await Action().PreviewAsync(Snapshots.ForPaths(input), Pages("1:x"), CancellationToken.None);

        Assert.True(preview.IsBlocked);
    }

    [Fact]
    public void Descriptor_IsLocalized()
    {
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("Seiten organisieren", Action().Descriptor.Title);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = original;
        }
    }
}
