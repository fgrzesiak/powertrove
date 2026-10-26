using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Ocr;
using PowerTrove.Abstractions.Testing;
using FileKit.Pdf;
using Xunit;

namespace PowerTrove.Pack.Pdf.Tests;

public class OcrPdfActionTests : ActionContractTestsBase
{
    private sealed class FakeTextRecognizer : ITextRecognizer
    {
        public IReadOnlyList<string> AvailableLanguages => new[] { "en-US", "de-DE" };

        public Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(int width, int height, byte[] bgra32, string? language, CancellationToken ct)
        {
            var word = new RecognizedWord("FakeWord", 10, 10, 100, 20);
            var line = new RecognizedLine("FakeWord", new[] { word });
            return Task.FromResult<IReadOnlyList<RecognizedLine>>(new[] { line });
        }
    }

    private sealed class FakePdfRenderer : IPdfPageRenderer
    {
        public bool IsAvailable => true;

        public Task<int> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken) => Task.FromResult(1);

        public Task<PageRaster> RenderGrayAsync(string path, int pageIndex, int maxWidthPx, CancellationToken cancellationToken)
        {
            return Task.FromResult(new PageRaster(1000, 1000, new byte[1000 * 1000]));
        }

        public Task RenderToDirectoryAsync(string pdfPath, string directory, string baseName, IReadOnlyList<int>? pageIndices, PdfRenderOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    protected override IAction CreateAction()
    {
        var services = FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
        return new OcrPdfAction(services, new FakePdfRenderer(), new FakeTextRecognizer());
    }

    protected override ContextSnapshot CreateValidSnapshot()
    {
        return Snapshots.ForPaths("document.pdf");
    }

    private ActionParameters CreateValidParameters()
    {
        return new DictionaryParameters(new Dictionary<string, string> { { "Language", "en-US" } });
    }

    [Fact]
    public async Task ProcessAsync_PagesWithoutText_AppendsInvisibleLayer()
    {
        var services = FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
        var action = new OcrPdfAction(services, new FakePdfRenderer(), new FakeTextRecognizer());

        using var tempDir = new TempDirectory();
        string inputPath = Path.Combine(tempDir.Path, "test.pdf");

        // Create PDF with no text
        var builder = new PdfDocumentBuilder();
        builder.AddBlankPage(100, 100);
        using (var fs = File.Create(inputPath))
        {
            builder.Save(fs);
        }

        var context = Snapshots.ForPaths(inputPath);
        var parameters = CreateValidParameters();

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);
        Assert.Single(receipt.OutputPaths);
        string outputPath = receipt.OutputPaths[0];

        // Parse result
        var doc = PdfDocument.Load(File.ReadAllBytes(outputPath), new PdfReadLimits());
        var contents = doc.Pages[0].Dictionary["Contents"] as PdfArray;
        Assert.NotNull(contents);
        var lastStreamRef = contents.Items.Last() as PdfReference;
        var lastStream = doc.GetObject(lastStreamRef!.ObjectNumber) as PdfStream;
        string streamData = System.Text.Encoding.ASCII.GetString(lastStream!.Data);

        // Verify that 3 Tr and Tz are present
        Assert.Contains("3 Tr", streamData);
        Assert.Contains("Tz", streamData);

        // Extract using PdfText.Extract
        string extracted = PdfText.Extract(doc, 0);
        Assert.Contains("FakeWord", extracted);
    }

    [Fact]
    public async Task ProcessAsync_PageWithText_IsSkipped()
    {
        var services = FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
        var action = new OcrPdfAction(services, new FakePdfRenderer(), new FakeTextRecognizer());

        using var tempDir = new TempDirectory();
        string inputPath = Path.Combine(tempDir.Path, "test.pdf");

        // Create PDF WITH text
        var builder = new PdfDocumentBuilder();
        var decs = new[] { new PdfTextDecoration("Existing Text", PdfAnchor.Center, 12, new PdfColor(0, 0, 0)) };
        var blankDoc = new PdfDocumentBuilder();
        blankDoc.AddBlankPage(100, 100);
        using var ms = new MemoryStream();
        blankDoc.Save(ms);
        var docWithBlank = PdfDocument.Load(ms.ToArray(), new PdfReadLimits());

        builder.AddPage(docWithBlank, 0, 0, decs);
        using (var fs = File.Create(inputPath))
        {
            builder.Save(fs);
        }

        var context = Snapshots.ForPaths(inputPath);
        var parameters = CreateValidParameters();

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);

        // All pages skipped
        // Skipped items produce no output and no failure; the base receipt only counts them.
        Assert.True(receipt.IsSuccess);
        Assert.Empty(receipt.OutputPaths);
        Assert.Empty(receipt.Failures);
    }
}
