using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions;
using ContextBar.Abstractions.Files;
using Xunit;
using FileKit.Pdf;

namespace ContextBar.Pack.Pdf.Tests;

public class RepairPdfActionTests
{
    private static byte[] CreateHealthyPdf()
    {
        using var stream = new MemoryStream();
        var builder = new PdfDocumentBuilder();
        builder.AddBlankPage(612, 792);
        builder.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public async Task ExecuteAsync_HealthyFile_SucceedsWithoutOutput()
    {
        using var tempDir = new TempDirectory();
        string healthyPath = Path.Combine(tempDir.Path, "healthy.pdf");
        File.WriteAllBytes(healthyPath, CreateHealthyPdf());

        var fs = new PhysicalOutputFileSystem();
        var services = new FileActionServices(fs, new OutputPlanner(fs), new ExtensionOnlyTypeVerifier());
        var action = new RepairPdfAction(services);
        Assert.False(PdfDocument.Load(File.ReadAllBytes(healthyPath)).WasRepaired);
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(healthyPath),
            Snapshots.Parameters(), null, CancellationToken.None);
        Assert.Empty(receipt.Failures);
        Assert.True(receipt.IsSuccess);
        Assert.Empty(receipt.OutputPaths);
        Assert.Contains("needed no changes", receipt.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ShiftedOffsets_RepairsAndWritesFile()
    {
        using var tempDir = new TempDirectory();
        string shiftedPath = Path.Combine(tempDir.Path, "shifted.pdf");
        var data = CreateHealthyPdf();
        int headerEnd = Array.IndexOf(data, (byte)'\n') + 1;
        File.WriteAllBytes(shiftedPath, data.Take(headerEnd).Concat(Encoding.ASCII.GetBytes("% padding comment\n")).Concat(data.Skip(headerEnd)).ToArray());

        var fs = new PhysicalOutputFileSystem();
        var services = new FileActionServices(fs, new OutputPlanner(fs), new ExtensionOnlyTypeVerifier());
        var action = new RepairPdfAction(services);
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(shiftedPath),
            Snapshots.Parameters(), null, CancellationToken.None);

        Assert.Empty(receipt.Failures);
        Assert.True(receipt.IsSuccess);
        string outputPath = Assert.Single(receipt.OutputPaths);
        Assert.Equal("shifted (repaired).pdf", Path.GetFileName(outputPath));

        var repairedDoc = PdfDocument.Load(File.ReadAllBytes(outputPath));
        Assert.False(repairedDoc.WasRepaired);
        Assert.Equal(1, repairedDoc.PageCount);
    }

    [Fact]
    public async Task ExecuteAsync_MissingXref_RepairsAndWritesFile()
    {
        using var tempDir = new TempDirectory();
        string missingXrefPath = Path.Combine(tempDir.Path, "missing.pdf");
        var data = CreateHealthyPdf();
        string text = Encoding.Latin1.GetString(data);
        text = text[..text.IndexOf("xref", StringComparison.Ordinal)];
        File.WriteAllBytes(missingXrefPath, Encoding.Latin1.GetBytes(text));

        var fs = new PhysicalOutputFileSystem();
        var services = new FileActionServices(fs, new OutputPlanner(fs), new ExtensionOnlyTypeVerifier());
        var action = new RepairPdfAction(services);
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(missingXrefPath),
            Snapshots.Parameters(), null, CancellationToken.None);

        Assert.Empty(receipt.Failures);
        Assert.True(receipt.IsSuccess);
        string outputPath = Assert.Single(receipt.OutputPaths);
        Assert.Equal("missing (repaired).pdf", Path.GetFileName(outputPath));

        var repairedDoc = PdfDocument.Load(File.ReadAllBytes(outputPath));
        Assert.False(repairedDoc.WasRepaired);
        Assert.Equal(1, repairedDoc.PageCount);
    }

    [Fact]
    public async Task ExecuteAsync_UnrepairableFile_FailsWithDamagedMessage()
    {
        using var tempDir = new TempDirectory();
        string garbagePath = Path.Combine(tempDir.Path, "garbage.pdf");
        File.WriteAllText(garbagePath, "This is not a PDF at all");

        var fs = new PhysicalOutputFileSystem();
        var services = new FileActionServices(fs, new OutputPlanner(fs), new ExtensionOnlyTypeVerifier());
        var action = new RepairPdfAction(services);
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(garbagePath),
            Snapshots.Parameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Empty(receipt.OutputPaths);
        Assert.Equal("The PDF is damaged and could not be read.", Assert.Single(receipt.Failures).ErrorMessage);
    }
}
