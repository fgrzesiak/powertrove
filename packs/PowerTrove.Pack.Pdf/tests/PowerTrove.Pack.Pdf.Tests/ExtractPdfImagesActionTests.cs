using System.Globalization;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf.Tests;

public class ExtractPdfImagesActionTests
{
    private static FileActionServices Services(IOutputFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new PhysicalOutputFileSystem();
        return new FileActionServices(fs, new OutputPlanner(fs), new ExtensionOnlyTypeVerifier());
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("gray.jpg")]
    public async Task JpegFixtures_AreExtractedUnchanged_WithStableNames(string fixture)
    {
        using var directory = new TempDirectory();
        byte[] jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        using var pdf = new MemoryStream();
        PdfTools.ImagesToPdf(new[] { PdfImage.Load(jpeg) }, pdf);
        byte[] input = pdf.ToArray();
        string path = directory.Write("private.pdf", input);

        var receipt = await new ExtractPdfImagesAction(Services()).ExecuteAsync(Snapshots.ForPaths(path),
            Snapshots.Parameters(("MinSize", "1")), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Empty(receipt.Failures);
        string folder = Assert.Single(receipt.OutputPaths);
        Assert.Equal("private (images)", Path.GetFileName(folder));
        string output = Assert.Single(Directory.GetFiles(folder));
        Assert.Equal("private - image 001.jpg", Path.GetFileName(output));
        Assert.Equal(jpeg, File.ReadAllBytes(output));
        Assert.Equal(input, File.ReadAllBytes(path));
        Assert.Contains("Extracted 1 image.", receipt.Message);
        Assert.DoesNotContain("private", receipt.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task FlateImages_AreExtractedAsLoadablePng(int components)
    {
        using var directory = new TempDirectory();
        string path = directory.Write("input.pdf", ImagePdf(components: components));
        var receipt = await new ExtractPdfImagesAction(Services()).ExecuteAsync(Snapshots.ForPaths(path),
            Snapshots.Parameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        string file = Assert.Single(Directory.GetFiles(Assert.Single(receipt.OutputPaths)));
        Assert.Equal(".png", Path.GetExtension(file));
        var image = PdfImage.Load(File.ReadAllBytes(file));
        Assert.Equal((32, 32), (image.Width, image.Height));
    }

    [Fact]
    public async Task SharedImageOnTwoPages_IsWrittenOnce_AndExistingFolderIsPreserved()
    {
        using var directory = new TempDirectory();
        var source = PdfDocument.Load(ImagePdf());
        using var output = new MemoryStream();
        new PdfDocumentBuilder().AddPage(source, 0).AddPage(source, 0).Save(output);
        var document = PdfDocument.Load(output.ToArray());
        Assert.Single(PdfImages.Enumerate(document));
        string path = directory.Write("input.pdf", output.ToArray());
        string existing = Path.Combine(directory.Path, "input (images)");
        Directory.CreateDirectory(existing);
        string sentinel = Path.Combine(existing, "keep.txt");
        File.WriteAllText(sentinel, "unchanged");

        var receipt = await new ExtractPdfImagesAction(Services()).ExecuteAsync(Snapshots.ForPaths(path),
            Snapshots.Parameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.NotEqual(existing, Assert.Single(receipt.OutputPaths));
        Assert.Single(Directory.GetFiles(receipt.OutputPaths[0]));
        Assert.Equal("unchanged", File.ReadAllText(sentinel));
        Assert.Contains("Extracted 1 image.", receipt.Message);
        Assert.DoesNotContain(directory.NewFiles(path), p => Path.GetFileName(p).StartsWith(".~cb", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsupportedAndSmallImages_AreCountedSeparately()
    {
        using var directory = new TempDirectory();
        var supported = PdfDocument.Load(ImagePdf());
        var small = PdfDocument.Load(ImagePdf(width: 10, height: 40));
        var unsupported = PdfDocument.Load(ImagePdf());
        var image = Assert.Single(PdfImages.Enumerate(unsupported));
        Assert.IsType<PdfStream>(unsupported.GetObject(image.Reference.ObjectNumber)).Dictionary["ColorSpace"] = new PdfName("DeviceCMYK");
        using var combined = new MemoryStream();
        PdfTools.Merge(new[] { supported, small, unsupported }, combined);
        string path = directory.Write("input.pdf", combined.ToArray());

        var receipt = await new ExtractPdfImagesAction(Services()).ExecuteAsync(Snapshots.ForPaths(path),
            Snapshots.Parameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Empty(receipt.Failures);
        Assert.Single(Directory.GetFiles(Assert.Single(receipt.OutputPaths)));
        Assert.Contains("Extracted 1 image.", receipt.Message);
        Assert.Contains("Skipped 1 unsupported image.", receipt.Message);
        Assert.Contains("Skipped 1 image below the minimum size.", receipt.Message);
    }

    [Fact]
    public async Task PdfWithoutImages_IsSkippedWithoutCreatingAnEmptyFolder()
    {
        using var directory = new TempDirectory();
        using var pdf = new MemoryStream();
        new PdfDocumentBuilder().AddBlankPage(200, 300).Save(pdf);
        string path = directory.Write("input.pdf", pdf.ToArray());
        var receipt = await new ExtractPdfImagesAction(Services()).ExecuteAsync(Snapshots.ForPaths(path),
            Snapshots.Parameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Empty(receipt.Failures);
        Assert.Empty(receipt.OutputPaths);
        Assert.Contains("No images found.", receipt.Message);
        Assert.Contains("needed no changes", receipt.Message);
        Assert.Empty(directory.NewFiles(path));
    }

    [Fact]
    public async Task AllImagesBelowMinimum_AreSkippedAndTemporaryFolderIsRemoved()
    {
        using var directory = new TempDirectory();
        string path = directory.Write("input.pdf", ImagePdf(width: 31, height: 40));
        var receipt = await new ExtractPdfImagesAction(Services()).ExecuteAsync(Snapshots.ForPaths(path),
            Snapshots.Parameters(), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess);
        Assert.Empty(receipt.OutputPaths);
        Assert.Empty(receipt.Failures);
        Assert.Contains("Skipped 1 image below the minimum size.", receipt.Message);
        Assert.Empty(directory.NewFiles(path));
    }

    [Fact]
    public async Task CancellationDuringFileCreation_RemovesTheWholeUncommittedFolder()
    {
        using var directory = new TempDirectory();
        using var canceled = new CancellationTokenSource();
        string path = directory.Write("input.pdf", ImagePdf());
        var fs = new CancelingFileSystem(canceled);
        var receipt = await new ExtractPdfImagesAction(Services(fs)).ExecuteAsync(Snapshots.ForPaths(path),
            Snapshots.Parameters(), null, canceled.Token);
        Assert.True(receipt.IsCanceled);
        Assert.False(receipt.IsSuccess);
        Assert.Empty(receipt.OutputPaths);
        Assert.Empty(receipt.Failures);
        Assert.Empty(directory.NewFiles(path));
    }

    [Fact]
    public async Task ReusingOneAction_DoesNotAccumulateEarlierCounts()
    {
        using var directory = new TempDirectory();
        string first = directory.Write("first.pdf", ImagePdf(count: 2));
        string second = directory.Write("second.pdf", ImagePdf());
        var action = new ExtractPdfImagesAction(Services());
        var firstReceipt = await action.ExecuteAsync(Snapshots.ForPaths(first), Snapshots.Parameters(), null, CancellationToken.None);
        var secondReceipt = await action.ExecuteAsync(Snapshots.ForPaths(second), Snapshots.Parameters(), null, CancellationToken.None);
        Assert.Contains("Extracted 2 images.", firstReceipt.Message);
        Assert.Contains("Extracted 1 image.", secondReceipt.Message);
    }

    [Fact]
    public async Task GermanDescriptorsPreviewAndReceipt_UseGermanResources()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            using var directory = new TempDirectory();
            string path = directory.Write("private.pdf", ImagePdf());
            var action = new ExtractPdfImagesAction(Services());
            var preview = await action.PreviewAsync(Snapshots.ForPaths(path), Snapshots.Parameters(), CancellationToken.None);
            var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), Snapshots.Parameters(), null, CancellationToken.None);
            Assert.Equal("Bilder aus PDF extrahieren", action.Descriptor.Title);
            Assert.Contains("Mindestbreite", Assert.Single(action.Descriptor.Parameters!).Title);
            Assert.Contains("Extrahiert", preview.PreviewText);
            Assert.Contains(preview.Warnings, w => w.Contains("übersprungen", StringComparison.Ordinal));
            Assert.Contains("1 Bild extrahiert.", receipt.Message);
            Assert.DoesNotContain("private", receipt.Message);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task ToolErrors_AreMappedToLocalizedFileNameFreeMessages()
    {
        using var directory = new TempDirectory();
        var document = PdfDocument.Load(ImagePdf());
        var image = Assert.Single(PdfImages.Enumerate(document));
        Assert.IsType<PdfStream>(document.GetObject(image.Reference.ObjectNumber)).Dictionary["Width"] = new PdfInteger(30_001);
        using var pdf = new MemoryStream();
        new PdfDocumentBuilder().AddPage(document, 0).Save(pdf);
        string path = directory.Write("private.pdf", pdf.ToArray());
        var receipt = await new ExtractPdfImagesAction(Services()).ExecuteAsync(Snapshots.ForPaths(path),
            Snapshots.Parameters(), null, CancellationToken.None);
        Assert.False(receipt.IsSuccess);
        Assert.Empty(receipt.OutputPaths);
        Assert.Equal("The PDF exceeds the supported size or complexity.", Assert.Single(receipt.Failures).ErrorMessage);
        Assert.DoesNotContain("private", receipt.Message);
        Assert.Empty(directory.NewFiles(path));
    }

    [Fact]
    public void ActionIsRegistered_WithoutOptionalRenderingEngines() =>
        Assert.Contains(new PdfPack(Services()).Actions, a => a.Descriptor.Id == "powertrove.pdf.extract-images");

    internal static byte[] ImagePdf(int width = 32, int height = 32, int components = 3, int count = 1)
    {
        using var png = new MemoryStream();
        PdfPngWriter.Write(png, width, height, components, Enumerable.Range(0, width * height * components).Select(i => (byte)i).ToArray());
        var images = Enumerable.Range(0, count).Select(_ => PdfImage.Load(png.ToArray())).ToArray();
        using var pdf = new MemoryStream();
        PdfTools.ImagesToPdf(images, pdf);
        return pdf.ToArray();
    }

    private sealed class CancelingFileSystem(CancellationTokenSource cancellation) : IOutputFileSystem
    {
        private readonly PhysicalOutputFileSystem _inner = new();
        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream CreateFile(string path) { var stream = _inner.CreateFile(path); cancellation.Cancel(); return stream; }
        public long GetFileLength(string path) => _inner.GetFileLength(path);
        public void MoveFile(string source, string target) => _inner.MoveFile(source, target);
        public void MoveDirectory(string source, string target) => _inner.MoveDirectory(source, target);
        public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);
    }
}

public class ExtractPdfImagesActionContracts : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _directory = new();
    protected override IAction CreateAction() => new ExtractPdfImagesAction(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()));
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(_directory.Write("input.pdf", ExtractPdfImagesActionTests.ImagePdf()));
    public void Dispose() => _directory.Dispose();
}
