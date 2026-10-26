using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions;
using ContextBar.Abstractions.Files;
using ContextBar.Abstractions.Testing;
using FileKit.Pdf;
using Xunit;

namespace ContextBar.Pack.Pdf.Tests;

public class CompressPdfActionTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FileActionServices _services = FileActionServices.CreatePhysical(new ContextBar.Infrastructure.Files.FileSignatureVerifier());

    private string Fixture(string name, string? copyAs = null)
    {
        string path = _dir.Write(copyAs ?? name, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
        return path;
    }

    [Fact]
    public async Task Balanced_OptimizesAndRecompressesImages()
    {
        string input = Fixture("linked-objstm.pdf");
        string imgPdf = _dir.File("img.pdf");

        byte[] jpgBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "photo.jpg"));
        var img = PdfImage.Load(jpgBytes, new PdfImageLimits { MaxFileBytes = 20_000_000 });
        using (var stream = File.Create(imgPdf))
        {
            var builder = new PdfDocumentBuilder();
            // Tiny page size (4 points = 0.055 inches) to make the estimated DPI > 150
            builder.AddBlankPage(4, 4);
            using var ms = new MemoryStream();
            builder.Save(ms);
            ms.Seek(0, SeekOrigin.Begin);
            var doc = PdfDocument.Load(ms.ToArray());
            PdfTools.StampImage(doc, img, stream, widthMm: 1.0);
        }

        var recompressor = new FakeRecompressor();
        var action = new CompressPdfAction(_services, recompressor);

        var parameters = new DictionaryParameters(new Dictionary<string, string> { ["Level"] = "balanced" });

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(imgPdf), parameters, null, CancellationToken.None);

        if (receipt.Failures.Count > 0)
        {
            Assert.True(receipt.IsSuccess, "Failure: " + receipt.Failures[0].ErrorMessage);
        }
        else
        {
            Assert.True(receipt.IsSuccess, $"Message: {receipt.Message}, Outputs: {receipt.OutputPaths.Count}, Canceled: {receipt.IsCanceled}");
        }
        Assert.True(recompressor.Called);
    }

    [Fact]
    public async Task OptimalFile_ThrowsFileSkippedException()
    {
        string input = Fixture("linked-objstm.pdf");
        // Create an optimal file by compressing it first
        string optimalPdf = _dir.File("optimal.pdf");
        var doc = PdfDocument.Open(input);
        using (var stream = File.Create(optimalPdf))
        {
            new PdfDocumentBuilder().AddPages(doc, Enumerable.Range(0, doc.PageCount)).Save(stream, new PdfWriteOptions { Compact = true });
        }

        var action = new CompressPdfAction(_services, new FakeRecompressor());

        var parameters = new DictionaryParameters(new Dictionary<string, string> { ["Level"] = "light" });

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(optimalPdf), parameters, null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, "Should be considered successful but skipped.");
        Assert.Contains("needed no changes", receipt.Message);
    }

    public void Dispose() => _dir.Dispose();

    private class FakeRecompressor : IPdfImageRecompressor
    {
        public bool Called { get; private set; }

        public Task<PdfImageReplacement?> RecompressAsync(byte[] encoded, string? filter, PdfImageInfo info, int maxWidth, int maxHeight, int quality, CancellationToken cancellationToken)
        {
            Called = true;
            // Return half dimensions
            return Task.FromResult<PdfImageReplacement?>(new PdfImageReplacement(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, info.Width / 2, info.Height / 2, 3));
        }
    }
}
