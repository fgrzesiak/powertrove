using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Pdf;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf.Tests;

/// <summary>Crop, resize and sanitize run through the registered pack actions on generated PDFs.</summary>
public sealed class PdfPageToolActionsTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IAction Action(string id) =>
        new PdfPack(FileActionServices.CreatePhysical(new FileSignatureVerifier())).Actions.Single(a => a.Descriptor.Id == id);

    private string Pdf(string name, params (double Width, double Height)[] pages)
    {
        var builder = new PdfDocumentBuilder();
        foreach (var (width, height) in pages) builder.AddBlankPage(width, height);
        using var stream = new MemoryStream();
        builder.Save(stream);
        return _dir.Write(name, stream.ToArray());
    }

    private static DictionaryParameters Parameters(params (string Key, string Value)[] values) =>
        new(values.ToDictionary(v => v.Key, v => v.Value));

    [Theory]
    [InlineData("powertrove.pdf.crop")]
    [InlineData("powertrove.pdf.resize-pages")]
    [InlineData("powertrove.pdf.sanitize")]
    public void PageTools_AreRegisteredWithLocalizedText(string id)
    {
        var action = Action(id);
        Assert.False(string.IsNullOrWhiteSpace(action.Descriptor.Title));
        Assert.NotEqual(action.Descriptor.Title, action.Descriptor.Description);
        Assert.DoesNotContain("_", action.Descriptor.Title);
    }

    [Fact]
    public async Task Crop_CreatesCroppedCopyAndKeepsSource()
    {
        string source = Pdf("scan.pdf", (595.28, 841.89), (595.28, 841.89));
        byte[] before = File.ReadAllBytes(source);

        var receipt = await Action("powertrove.pdf.crop").ExecuteAsync(
            Snapshots.ForPaths(source), Parameters(("Margin", "10"), ("Pages", "all")), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);
        Assert.Equal(_dir.File("scan (cropped).pdf"), Assert.Single(receipt.OutputPaths));
        var result = PdfDocument.Open(receipt.OutputPaths[0]);
        Assert.Equal(2, result.PageCount);
        var (width, height) = PdfTools.GetPageSize(result, 1);
        double margin = 10 * 72 / 25.4;
        Assert.Equal(595.28 - 2 * margin, width, 1);
        Assert.Equal(841.89 - 2 * margin, height, 1);
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task Resize_PutsEachPageOnTargetSizeInItsOrientation()
    {
        string source = Pdf("slides.pdf", (960, 540), (612, 792));

        var receipt = await Action("powertrove.pdf.resize-pages").ExecuteAsync(
            Snapshots.ForPaths(source), Parameters(("Size", "a4"), ("Fit", "fit")), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);
        Assert.Equal(_dir.File("slides (resized).pdf"), Assert.Single(receipt.OutputPaths));
        var result = PdfDocument.Open(receipt.OutputPaths[0]);
        Assert.Equal(841.89, PdfTools.GetPageSize(result, 0).Width, 1);
        Assert.Equal(595.28, PdfTools.GetPageSize(result, 0).Height, 1);
        Assert.Equal(595.28, PdfTools.GetPageSize(result, 1).Width, 1);
        Assert.Equal(841.89, PdfTools.GetPageSize(result, 1).Height, 1);
    }

    [Fact]
    public async Task Sanitize_RemovesMetadataAndPreviewOmitsFileName()
    {
        string plain = Pdf("private-name.pdf", (612, 792));
        string source = _dir.File("private-name (with info).pdf");
        using (var output = File.Create(source))
            PdfTools.EditMetadata(PdfDocument.Open(plain), output, "Title", "Author", null, null);
        var action = Action("powertrove.pdf.sanitize");
        var parameters = Parameters(("KeepLinks", "true"));

        var preview = await action.PreviewAsync(Snapshots.ForPaths(source), parameters, CancellationToken.None);
        Assert.NotEmpty(preview.Warnings);
        Assert.All(preview.Warnings, warning => Assert.DoesNotContain("private-name", warning));

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(source), parameters, null, CancellationToken.None);
        Assert.True(receipt.IsSuccess, receipt.Message);
        var result = PdfDocument.Open(Assert.Single(receipt.OutputPaths));
        Assert.False(PdfSanitizer.Analyze(result).HasHiddenContent);
    }

    [Fact]
    public async Task Sanitize_SkipsPdfWithNothingToRemove()
    {
        // A sanitized copy has no document information left, so a second run has nothing to remove.
        var action = Action("powertrove.pdf.sanitize");
        var parameters = Parameters(("KeepLinks", "true"));
        var first = await action.ExecuteAsync(Snapshots.ForPaths(Pdf("clean.pdf", (612, 792))), parameters, null, CancellationToken.None);
        string sanitized = Assert.Single(first.OutputPaths);

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(sanitized), parameters, null, CancellationToken.None);

        Assert.Empty(receipt.OutputPaths);
        Assert.False(File.Exists(_dir.File("clean (sanitized) (sanitized).pdf")));
    }
}
