using Windows.Data.Pdf;
using Windows.Storage;
using PowerTrove.Pack.Markdown.Markdig;

namespace PowerTrove.Pack.Markdown.Tests;

/// <summary>Real Microsoft Edge printing on this Windows machine, as the installed pack does it.</summary>
public sealed class MarkdownPdfNativeTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task EdgePrintsMarkdownWithTableAndEmbeddedLocalImage()
    {
        var printer = new MarkdownPdfPrinter();
        Assert.True(printer.IsAvailable, "Microsoft Edge is required for this native test.");
        _dir.Write("img/dot.png", MarkdownActionTests.Png());
        string path = _dir.Write("report.md", "# Bericht\n\nGrüße aus **PowerTrove**.\n\n| A | B |\n|---|--:|\n| 1 | 2 |\n\n![dot](img/dot.png)\n\n- [x] fertig\n");
        var action = new MarkdownToPdfAction(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()), new MarkdigMarkdownEngine(), printer);

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, default);

        Assert.True(receipt.IsSuccess, string.Join("; ", receipt.Failures.Select(f => f.ErrorMessage)));
        string output = Assert.Single(receipt.OutputPaths);
        Assert.True(File.ReadAllBytes(output).AsSpan().StartsWith("%PDF-"u8));
        var pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(output));
        Assert.Equal(1u, pdf.PageCount);
        using var page = pdf.GetPage(0);
        // A4 in 96-DPI device-independent pixels.
        Assert.InRange(page.Size.Width, 792, 796); Assert.InRange(page.Size.Height, 1121, 1125);
    }
}
