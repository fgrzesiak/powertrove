using System.Text;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Pdf;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf.Tests;

public sealed class PdfActionsTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static FileActionServices Services() => FileActionServices.CreatePhysical(new FileSignatureVerifier());

    private static IAction Action(string id, IPdfPageRenderer? renderer = null) =>
        new PdfPack(Services(), renderer).Actions.Single(a => a.Descriptor.Id == id);

    private string Fixture(string name, string? as_ = null) =>
        _dir.Write(as_ ?? name, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    private static int Pages(string path) => PdfDocument.Open(path).PageCount;

    [Fact]
    public async Task Interleave_UnequalPages_PreviewWarns()
    {
        string first = Fixture("linked-objstm.pdf", "f.pdf"); // 3 pages
        string second = _dir.Write("s.pdf", File.ReadAllBytes(Fixture("linked-objstm.pdf")));
        using (var fs = File.Create(second))
        {
            FileKit.Pdf.PdfTools.ExtractPages(FileKit.Pdf.PdfDocument.Open(first), new[] { 0 }, fs);
        }

        var action = Action("powertrove.pdf.interleave");
        var preview = await action.PreviewAsync(Snapshots.ForPaths(first, second), new DictionaryParameters(new Dictionary<string, string>()), CancellationToken.None);

        Assert.Contains(preview.Warnings, w => w.Contains("different number of pages"));
    }

    [Fact]
    public async Task StampPdfImageAction_Preview_WithInvalidImage_Blocks()
    {
        string input = Fixture("linked-objstm.pdf");
        string image = _dir.Write("corrupt.png", new byte[] { 0x01, 0x02, 0x03 });

        var action = Action("powertrove.pdf.stamp-image");
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            ["Image"] = image
        });

        var preview = await action.PreviewAsync(Snapshots.ForPaths(input), parameters, CancellationToken.None);

        Assert.True(preview.IsBlocked);
        Assert.Contains("Only JPEG and PNG images", preview.PreviewText, StringComparison.OrdinalIgnoreCase);
    }

    private class DelayingRenderer : IPdfPageRenderer
    {
        public bool IsAvailable => true;
        public readonly System.Threading.ManualResetEventSlim Gate = new(false);

        public async Task<int> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken)
        {
            await Task.Run(() => Gate.Wait(cancellationToken), cancellationToken);
            return 3;
        }

        public Task RenderToDirectoryAsync(string pdfPath, string directory, string baseName, IReadOnlyList<int>? pageIndices, PdfRenderOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<PageRaster> RenderGrayAsync(string path, int pageIndex, int maxWidthPx, CancellationToken cancellationToken)
            => Task.FromResult(new PageRaster(1, 1, new byte[1]));
    }

    [Fact]
    public async Task InterleavePdfAction_Preview_RunsOffThreadAndStopsOnCancel()
    {
        string first = Fixture("linked-objstm.pdf", "f.pdf");
        string second = Fixture("linked-objstm.pdf", "s.pdf");

        var renderer = new DelayingRenderer();
        var action = Action("powertrove.pdf.interleave", renderer);
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            ["ReverseSecond"] = "true"
        });

        using var cts = new CancellationTokenSource();
        var task = action.PreviewAsync(Snapshots.ForPaths(first, second), parameters, cts.Token).AsTask();

        // Ensure it is running off thread and blocking at our gate
        Assert.False(task.IsCompleted);

        // Cancel it
        cts.Cancel();

        // It should throw OperationCanceledException instead of returning a blocked preview
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task StampPdfImageAction_Preview_WithValidImage_Previews()
    {
        string input = Fixture("linked-objstm.pdf");
        byte[] validPng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0, 144, 119, 83, 222, 0, 0, 0, 12, 73, 68, 65, 84, 8, 215, 99, 248, 255, 255, 63, 0, 5, 254, 2, 254, 220, 204, 89, 231, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 };
        string image = _dir.Write("valid.png", validPng);

        var action = Action("powertrove.pdf.stamp-image");
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            ["Image"] = image
        });



        var preview = await action.PreviewAsync(Snapshots.ForPaths(input), parameters, CancellationToken.None);

        Assert.False(preview.IsBlocked);
    }




    [Fact]
    public async Task StampSelectedImagePdfAction_Executes()
    {
        string pdfPath = Fixture("linked-objstm.pdf");
        byte[] validPng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0, 144, 119, 83, 222, 0, 0, 0, 12, 73, 68, 65, 84, 8, 215, 99, 248, 255, 255, 63, 0, 5, 254, 2, 254, 220, 204, 89, 231, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 };
        string imagePath = _dir.Write("test_image.png", validPng);

        var action = Action("powertrove.pdf.stamp-selected-image");
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            ["Position"] = "bottom-center",
            ["Width"] = "50",
            ["Pages"] = "all",
            ["Opacity"] = "100"
        });

        var context = Snapshots.ForPaths(pdfPath, imagePath);
        var preview = await action.PreviewAsync(context, parameters, CancellationToken.None);
        Assert.False(preview.IsBlocked);

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);
        Assert.True(receipt.IsSuccess);
        Assert.Single(receipt.OutputPaths);
        Assert.EndsWith("(stamped).pdf", receipt.OutputPaths[0]);
    }

    [Fact]
    public void StampSelectedImagePdfAction_SelectionFit()
    {
        var action = Action("powertrove.pdf.stamp-selected-image");

        // 1 PDF, 1 Image
        var context1 = Snapshots.ForPaths(Fixture("linked-objstm.pdf"), _dir.Write("test_image.png", new byte[0]));
        Assert.Equal(SelectionFit.Ideal, SelectionRequirements.Evaluate(action.Descriptor.Selection!, context1));

        // 2 PDFs, 1 Image
        var context2 = Snapshots.ForPaths(Fixture("linked-objstm.pdf"), Fixture("linked-objstm.pdf", "second.pdf"), _dir.Write("test_image2.png", new byte[0]));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(action.Descriptor.Selection!, context2));

        // 1 PDF, 0 Image
        var context3 = Snapshots.ForPaths(Fixture("linked-objstm.pdf"));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(action.Descriptor.Selection!, context3));
    }

    [Fact]
    public void StampSelectedImagePdfAction_LocalizedTitle()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");
            var action = Action("powertrove.pdf.stamp-selected-image");
            Assert.Equal("Bild auf PDF platzieren", action.Descriptor.Title);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public async Task InsertBlankPages_HandlesAllPositions()
    {
        string input = Fixture("linked-objstm.pdf"); // 3 pages
        var action = Action("powertrove.pdf.insert-blank");
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            ["Mode"] = "at-positions",
            ["Pages"] = "0, 1-2, last"
        });

        await action.ExecuteAsync(Snapshots.ForPaths(input), parameters, null, CancellationToken.None);

        string output = Directory.GetFiles(_dir.Path, "* (blanks).pdf").Single();
        Assert.Equal(3 + 1 + 2 + 1, Pages(output)); // 3 original + 4 blanks
    }

    [Fact]
    public async Task InsertBlankPages_BeyondPageCount_Fails()
    {
        string input = Fixture("linked-objstm.pdf"); // 3 pages
        var action = Action("powertrove.pdf.insert-blank");
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            ["Mode"] = "at-positions",
            ["Pages"] = "4-"
        });

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(input), parameters, null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Contains(receipt.Failures, f => f.ErrorMessage.Contains("between 0 and") || f.ErrorMessage.Contains("A page range must go from a lower to a higher position."));
    }

    [Fact]
    public async Task InsertBlankPages_ReversedRange_Fails()
    {
        string input = Fixture("linked-objstm.pdf"); // 3 pages
        var action = Action("powertrove.pdf.insert-blank");
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            ["Mode"] = "at-positions",
            ["Pages"] = "2-1"
        });

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(input), parameters, null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Contains(receipt.Failures, f => f.ErrorMessage.Contains("lower to a higher"));
    }

    [Fact]
    public async Task SplitPdf_OpenEndedBeyondPageCount_FailsAndLeavesNoOutput()
    {
        string input = Fixture("linked-objstm.pdf"); // 3 pages
        var action = Action("powertrove.pdf.split");
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            ["Mode"] = "ranges",
            ["Ranges"] = "1-2; 4-"
        });

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(input), parameters, null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Contains(receipt.Failures, f => f.ErrorMessage.Contains("between 1 and 3"));
        Assert.Empty(Directory.GetDirectories(_dir.Path)); // No output folder left
    }

    [Fact]
    public async Task Insert_AfterPageBeyondCount_FailsWithClearMessage()
    {
        string first = Fixture("linked-objstm.pdf", "1.pdf"); // target, 3 pages
        string second = Fixture("linked-objstm.pdf", "2.pdf"); // source

        var action = Action("powertrove.pdf.insert");
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            ["Position"] = "after",
            ["AfterPage"] = "10"
        });

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(first, second), parameters, null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Single(receipt.Failures);
        Assert.Contains("target PDF has fewer pages", receipt.Failures[0].ErrorMessage);
    }

    [Fact]
    public async Task Interleave_SelectionRequirements_Rejects1Or3()
    {
        var action = Action("powertrove.pdf.interleave");
        var reqs = action.Descriptor.Selection!;

        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(reqs, Snapshots.ForPaths("1.pdf")));
        Assert.Equal(SelectionFit.Ideal, SelectionRequirements.Evaluate(reqs, Snapshots.ForPaths("1.pdf", "2.pdf")));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(reqs, Snapshots.ForPaths("1.pdf", "2.pdf", "3.pdf")));
    }

    [Fact]
    public async Task Merge_CombinesInNaturalNameOrder()
    {
        string second = Fixture("linked-objstm.pdf", "part10.pdf");
        string first = Fixture("linked-objstm.pdf", "part2.pdf");

        var receipt = await Action("powertrove.pdf.merge").ExecuteAsync(Snapshots.ForPaths(second, first), new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);
        string output = Assert.Single(receipt.OutputPaths);
        Assert.Equal(_dir.File("part2 (merged).pdf"), output);
        Assert.Equal(6, Pages(output));
    }

    [Fact]
    public async Task Merge_EncryptedInput_FailsWithoutOutput()
    {
        string good = Fixture("linked-objstm.pdf", "a.pdf");
        byte[] data = File.ReadAllBytes(good);
        string text = Encoding.Latin1.GetString(data);
        // Turn the trailer into an encrypted one (the xref stream dictionary carries the trailer).
        text = text.Replace("/Type/XRef", "/Type/XRef/Encrypt<</Filter/Standard>>").Replace("/Type /XRef", "/Type /XRef /Encrypt << /Filter /Standard >>");
        string encrypted = _dir.Write("b.pdf", Encoding.Latin1.GetBytes(text));

        var receipt = await Action("powertrove.pdf.merge").ExecuteAsync(Snapshots.ForPaths(good, encrypted), new EmptyParameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Empty(receipt.OutputPaths);
        Assert.Contains("encrypted", Assert.Single(receipt.Failures).ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { good, encrypted }.OrderBy(p => p), _dir.NewFiles().OrderBy(p => p));
    }

    [Fact]
    public async Task Merge_NeedsTwoPdfs()
    {
        string only = Fixture("linked-objstm.pdf", "a.pdf");
        var availability = await Action("powertrove.pdf.merge").MatchAsync(Snapshots.ForPaths(only), CancellationToken.None);
        Assert.False(availability.IsAvailable);
    }

    [Fact]
    public async Task Split_WritesOnePdfPerPageIntoNewFolder()
    {
        string pdf = Fixture("linked-objstm.pdf", "report.pdf");

        var receipt = await Action("powertrove.pdf.split").ExecuteAsync(Snapshots.ForPaths(pdf), new EmptyParameters(), null, CancellationToken.None);

        string folder = Assert.Single(receipt.OutputPaths);
        Assert.Equal(_dir.File("report (pages)"), folder);
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "report - page 01.pdf", "report - page 02.pdf", "report - page 03.pdf" }, files);
        Assert.All(Directory.GetFiles(folder), f => Assert.Equal(1, Pages(f)));
    }

    [Fact]
    public async Task Reorder_CustomOrderAndRanges_ReordersCorrectly()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");

        // The document linked-objstm.pdf has 3 pages, so let's use 3,1,2
        var receipt = await Action("powertrove.pdf.reorder").ExecuteAsync(Snapshots.ForPaths(pdf), Snapshots.Parameters(("Mode", "custom"), ("Order", "3,1,2")), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);
        string output = Assert.Single(receipt.OutputPaths);
        Assert.Equal(_dir.File("doc (reordered).pdf"), output);
        Assert.Equal(3, Pages(output));
    }

    [Fact]
    public async Task Reorder_ReverseMode_Works()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");
        var receipt = await Action("powertrove.pdf.reorder").ExecuteAsync(Snapshots.ForPaths(pdf), Snapshots.Parameters(("Mode", "reverse")), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess, receipt.Message);
    }

    [Fact]
    public async Task Reorder_OddEven_EvenOdd_Works()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");
        var receiptOdd = await Action("powertrove.pdf.reorder").ExecuteAsync(Snapshots.ForPaths(pdf), Snapshots.Parameters(("Mode", "odd-even")), null, CancellationToken.None);
        Assert.True(receiptOdd.IsSuccess, receiptOdd.Message);

        var receiptEven = await Action("powertrove.pdf.reorder").ExecuteAsync(Snapshots.ForPaths(pdf), Snapshots.Parameters(("Mode", "even-odd")), null, CancellationToken.None);
        Assert.True(receiptEven.IsSuccess, receiptEven.Message);
    }

    [Fact]
    public async Task Reorder_MissingPage_FailsWithClearMessage()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");
        var receipt = await Action("powertrove.pdf.reorder").ExecuteAsync(Snapshots.ForPaths(pdf), Snapshots.Parameters(("Mode", "custom"), ("Order", "1,2")), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Equal("Page 3 is missing.", Assert.Single(receipt.Failures).ErrorMessage);
    }

    [Fact]
    public async Task Reorder_DuplicatePage_FailsWithClearMessage()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");
        var receipt = await Action("powertrove.pdf.reorder").ExecuteAsync(Snapshots.ForPaths(pdf), Snapshots.Parameters(("Mode", "custom"), ("Order", "1,2,2")), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Equal("Page 2 is listed twice.", Assert.Single(receipt.Failures).ErrorMessage);
    }

    [Fact]
    public async Task Reorder_SyntaxError_BlocksPreview()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");
        var preview = await Action("powertrove.pdf.reorder").PreviewAsync(Snapshots.ForPaths(pdf), Snapshots.Parameters(("Mode", "custom"), ("Order", "abc")), CancellationToken.None);
        Assert.True(preview.IsBlocked);
    }

    [Fact]
    public async Task ExtractPages_UsesGivenOrder()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");

        var receipt = await Action("powertrove.pdf.extract-pages").ExecuteAsync(Snapshots.ForPaths(pdf), Snapshots.Parameters(("Pages", "3, 1")), null, CancellationToken.None);

        string output = Assert.Single(receipt.OutputPaths);
        Assert.Equal(_dir.File("doc (selected pages).pdf"), output);
        Assert.Equal(2, Pages(output));
    }

    [Fact]
    public async Task ExtractPages_OutOfRange_FailsPerFileWithClearMessage()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");

        var receipt = await Action("powertrove.pdf.extract-pages").ExecuteAsync(Snapshots.ForPaths(pdf), Snapshots.Parameters(("Pages", "2-9")), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Equal("Page numbers must be between 1 and 3.", Assert.Single(receipt.Failures).ErrorMessage);
    }

    [Fact]
    public async Task ExtractPages_InvalidSyntax_BlocksPreview()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");

        var preview = await Action("powertrove.pdf.extract-pages").PreviewAsync(Snapshots.ForPaths(pdf), Snapshots.Parameters(("Pages", "one to three")), CancellationToken.None);

        Assert.True(preview.IsBlocked);
    }

    [Fact]
    public async Task RemovePages_KeepsTheRest()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");

        var receipt = await Action("powertrove.pdf.remove-pages").ExecuteAsync(Snapshots.ForPaths(pdf), Snapshots.Parameters(("Pages", "2")), null, CancellationToken.None);

        Assert.Equal(2, Pages(Assert.Single(receipt.OutputPaths)));
    }

    [Fact]
    public async Task Rotate_SelectedPages()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");

        var receipt = await Action("powertrove.pdf.rotate").ExecuteAsync(Snapshots.ForPaths(pdf), Snapshots.Parameters(("Angle", "180"), ("Pages", "2")), null, CancellationToken.None);

        var document = PdfDocument.Open(Assert.Single(receipt.OutputPaths));
        Assert.Equal(new[] { 0, 180, 0 }, document.Pages.Select(p => p.Rotate));
    }

    [Fact]
    public async Task Rotate_AllPagesByDefault()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");

        var receipt = await Action("powertrove.pdf.rotate").ExecuteAsync(Snapshots.ForPaths(pdf), new EmptyParameters(), null, CancellationToken.None);

        Assert.All(PdfDocument.Open(receipt.OutputPaths[0]).Pages, p => Assert.Equal(90, p.Rotate));
    }

    [Fact]
    public async Task AddPageNumbers_ExecutesSuccessfully()
    {
        var pdf = Fixture("linked-objstm.pdf", "test-3p.pdf");
        var document = FileKit.Pdf.PdfDocument.Load(File.ReadAllBytes(pdf));

        try {
            using var outputStream = new MemoryStream();
            FileKit.Pdf.PdfTools.AddPageNumbers(document, outputStream, FileKit.Pdf.PdfAnchor.BottomCenter, FileKit.Pdf.PdfPageNumberFormat.PageNumberOfTotal, 2, false, 10);
        } catch(Exception e) {
            System.Console.WriteLine($"Exception in library: {e}");
        }

        var receipt = await Action("powertrove.pdf.page-numbers").ExecuteAsync(
            Snapshots.ForPaths(pdf),
            Snapshots.Parameters(("Format", "n-of-total"), ("StartAt", "2"), ("Position", "bottom-center"), ("SkipFirst", "false"), ("FontSize", "10")),
            null, CancellationToken.None);
        Assert.True(receipt.IsSuccess, $"Action failed. Output messages: {string.Join(", ", receipt.Failures.Select(f => f.ErrorMessage))}");
        Assert.Single(receipt.OutputPaths);
        Assert.EndsWith(" (numbered).pdf", receipt.OutputPaths[0]);

        var doc = FileKit.Pdf.PdfDocument.Load(File.ReadAllBytes(receipt.OutputPaths[0]));
        Assert.Equal(3, doc.PageCount);

        var contents1 = (FileKit.Pdf.PdfArray)doc.Resolve(doc.Pages[0].Dictionary["Contents"])!;
        var decStream1 = (FileKit.Pdf.PdfStream)doc.Resolve(contents1.Items.Last())!;
        string data1 = System.Text.Encoding.ASCII.GetString(decStream1.Data);
        // Decoration text is written as a WinAnsi hex string (CB-170).
        Assert.Contains("<" + Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("Page 2 of 3")) + "> Tj", data1);
    }

    [Fact]
    public async Task RemoveMetadata_DropsDocumentInfo()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");
        Assert.NotNull(PdfDocument.Open(pdf).Info);

        var receipt = await Action("powertrove.pdf.remove-metadata").ExecuteAsync(Snapshots.ForPaths(pdf), new EmptyParameters(), null, CancellationToken.None);

        Assert.Null(PdfDocument.Open(Assert.Single(receipt.OutputPaths)).Info);
    }

    [Fact]
    public async Task ImagesToPdf_OnePagePerImage()
    {
        string b = Fixture("photo.jpg", "scan 2.jpg");
        string a = Fixture("gray.jpg", "scan 1.jpg");

        var receipt = await Action("powertrove.pdf.images-to-pdf").ExecuteAsync(Snapshots.ForPaths(b, a), Snapshots.Parameters(("PageSize", "letter")), null, CancellationToken.None);

        string output = Assert.Single(receipt.OutputPaths);
        Assert.Equal(2, Pages(output));
        var (width, height) = PdfTools.GetPageSize(PdfDocument.Open(output), 0);
        Assert.True(width == 612 || width == 792);
    }

    [Fact]
    public async Task PdfToImages_UsesRendererAndNewFolder()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");
        var renderer = new FakeRenderer();

        var receipt = await Action("powertrove.pdf.to-images", renderer).ExecuteAsync(Snapshots.ForPaths(pdf), Snapshots.Parameters(("Pages", "1-2"), ("Dpi", "300")), null, CancellationToken.None);

        string folder = Assert.Single(receipt.OutputPaths);
        Assert.Equal(_dir.File("doc (images)"), folder);
        Assert.Equal(new[] { 0, 1 }, renderer.LastPages);
        Assert.Equal(300, renderer.LastOptions!.Dpi);
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public async Task PdfToImages_WithoutRenderer_IsUnavailable()
    {
        string pdf = Fixture("linked-objstm.pdf", "doc.pdf");
        var availability = await Action("powertrove.pdf.to-images").MatchAsync(Snapshots.ForPaths(pdf), CancellationToken.None);
        Assert.False(availability.IsAvailable);
    }

    [Fact]
    public async Task Info_ShowsCountsWithoutContent()
    {
        string pdf = Fixture("linked-objstm.pdf", "confidential.pdf");

        var receipt = await Action("powertrove.pdf.info").ExecuteAsync(Snapshots.ForPaths(pdf), new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Contains("3 page(s)", receipt.Message);
        Assert.Contains("148 x 210 mm", receipt.Message);
        Assert.DoesNotContain("confidential", receipt.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(receipt.OutputPaths);
    }

    [Fact]
    public async Task DamagedPdf_IsReportedPerFile()
    {
        string broken = _dir.Write("broken.pdf", "%PDF-1.7\nthis is not really a pdf");

        var receipt = await Action("powertrove.pdf.rotate").ExecuteAsync(Snapshots.ForPaths(broken), new EmptyParameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Equal("The PDF is damaged and could not be read.", Assert.Single(receipt.Failures).ErrorMessage);
    }

    private sealed class FakeRenderer : IPdfPageRenderer
    {
        public IReadOnlyList<int>? LastPages { get; private set; }
        public PdfRenderOptions? LastOptions { get; private set; }
        public bool IsAvailable => true;

        public HashSet<int> BlankPages { get; } = new();

        public Task<int> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken) => Task.FromResult(FileKit.Pdf.PdfDocument.Open(pdfPath).PageCount);

        public Task RenderToDirectoryAsync(string pdfPath, string directory, string baseName, IReadOnlyList<int>? pageIndices, PdfRenderOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            LastPages = pageIndices;
            LastOptions = options;
            foreach (int page in pageIndices ?? Array.Empty<int>())
                File.WriteAllText(Path.Combine(directory, $"{baseName} - page {page + 1}.png"), "png");
            return Task.CompletedTask;
        }

        public Task<PageRaster> RenderGrayAsync(string path, int pageIndex, int maxWidthPx, CancellationToken cancellationToken)
        {
            byte val = BlankPages.Contains(pageIndex) ? (byte)255 : (byte)0;
            byte[] pixels = new byte[100 * 100];
            Array.Fill(pixels, val);
            return Task.FromResult(new PageRaster(100, 100, pixels));
        }
    }

    [Fact]
    public void GermanTranslations_AreAppliedWhenCultureIsDe()
    {
        var action = new MergePdfAction(FileActionServices.CreatePhysical(new FileSignatureVerifier()));

        var originalCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");
            action = new MergePdfAction(FileActionServices.CreatePhysical(new FileSignatureVerifier()));
            Assert.Equal("PDFs zusammenführen", action.Descriptor.Title);
            Assert.Equal("Lesezeichen für jede Datei erstellen", action.Descriptor.Parameters![0].Title);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public async Task MergePdf_WithAddBookmarks_CreatesBookmarks()
    {
        Fixture("linked-objstm.pdf", "a.pdf"); string inputA = _dir.File("a.pdf");
        Fixture("linked-objstm.pdf", "b.pdf"); string inputB = _dir.File("b.pdf");

        var action = new MergePdfAction(Services());
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(inputA, inputB), Snapshots.Parameters(("AddBookmarks", "true")), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Single(receipt.OutputPaths);

        string output = receipt.OutputPaths[0];
        var document = FileKit.Pdf.PdfDocument.Load(System.IO.File.ReadAllBytes(output));
        var outlines = FileKit.Pdf.PdfOutline.Read(document);

        Assert.Equal(2, outlines.Count);
        Assert.Equal("a", outlines[0].Title);
        Assert.Equal("b", outlines[1].Title);
    }

    [Fact]
    public async Task SplitPdf_Single_OutputsSinglePages()
    {
        Fixture("linked-objstm.pdf", "a.pdf"); string input = _dir.File("a.pdf");
        var action = new SplitPdfAction(Services());
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(input), Snapshots.Parameters(("Mode", "single")), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess);
        Assert.Single(receipt.OutputPaths);
        string folder = receipt.OutputPaths[0];
        Assert.Equal("a (pages)", Path.GetFileName(folder));
        var files = Directory.GetFiles(folder, "*.pdf");
        Assert.Equal(3, files.Length);
        Assert.Contains("a - page 01.pdf", files.Select(Path.GetFileName));
    }

    [Fact]
    public async Task SplitPdf_Ranges_OutputsRanges()
    {
        Fixture("linked-objstm.pdf", "a.pdf"); string input = _dir.File("a.pdf");
        var action = new SplitPdfAction(Services());
        var parameters = Snapshots.Parameters(("Mode", "ranges"), ("Ranges", "1-2; 3-"));
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(input), parameters, null, CancellationToken.None);
        Assert.True(receipt.IsSuccess);
        string folder = receipt.OutputPaths[0];
        Assert.Equal("a (split)", Path.GetFileName(folder));
        var files = Directory.GetFiles(folder, "*.pdf");
        Assert.Equal(2, files.Length);
        Assert.Contains("a - part 1.pdf", files.Select(Path.GetFileName));
        Assert.Contains("a - part 2.pdf", files.Select(Path.GetFileName));

    }

    [Fact]
    public async Task SplitPdf_Every_OutputsChunks()
    {
        Fixture("linked-objstm.pdf", "a.pdf"); string input = _dir.File("a.pdf");
        var action = new SplitPdfAction(Services());
        var parameters = Snapshots.Parameters(("Mode", "every"), ("Every", "2"));
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(input), parameters, null, CancellationToken.None);
        Assert.True(receipt.IsSuccess);
        string folder = receipt.OutputPaths[0];
        var files = Directory.GetFiles(folder, "*.pdf");
        Assert.Equal(2, files.Length);
        Assert.Contains("a - part 1.pdf", files.Select(Path.GetFileName));

    }

    [Fact]
    public async Task SplitPdf_OddEven_OutputsOddEven()
    {
        Fixture("linked-objstm.pdf", "a.pdf"); string input = _dir.File("a.pdf");
        var action = new SplitPdfAction(Services());
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(input), Snapshots.Parameters(("Mode", "odd-even")), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess);
        string folder = receipt.OutputPaths[0];
        var files = Directory.GetFiles(folder, "*.pdf");
        Assert.Equal(2, files.Length);
        Assert.Contains("a - odd pages.pdf", files.Select(Path.GetFileName));
        Assert.Contains("a - even pages.pdf", files.Select(Path.GetFileName));
    }

    [Fact]
    public async Task RemoveBlankPages_RemovesBlank_SkipsIfNone_ThrowsIfAll()
    {
        var services = FileActionServices.CreatePhysical(new FileSignatureVerifier());
        var renderer = new FakeRenderer();
        var action = new RemoveBlankPagesPdfAction(services, renderer);

        var parameters = new DictionaryParameters(new Dictionary<string, string> { { "Sensitivity", "normal" } });
        var context = Snapshots.ForPaths(Fixture("linked-objstm.pdf"));
        // The batch base turns FileSkippedException/FileProcessingException into receipt entries.
        var none = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);
        Assert.True(none.IsSuccess);
        Assert.Empty(none.OutputPaths);

        renderer.BlankPages.Add(0);
        renderer.BlankPages.Add(1);
        renderer.BlankPages.Add(2);
        var all = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);
        Assert.False(all.IsSuccess);
        Assert.Equal("All pages are blank.", Assert.Single(all.Failures).ErrorMessage);

        renderer.BlankPages.Clear();
        renderer.BlankPages.Add(1);
        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);
        Assert.True(receipt.IsSuccess);
        Assert.Single(receipt.OutputPaths);
        Assert.EndsWith("(cleaned).pdf", receipt.OutputPaths[0]);

        var document = FileKit.Pdf.PdfDocument.Open(receipt.OutputPaths[0]);
        Assert.Equal(2, document.PageCount);
    }
}

public sealed class MergePdfContractTests : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _dir = new();

    protected override IAction CreateAction() => new MergePdfAction(FileActionServices.CreatePhysical(new FileSignatureVerifier()));

    protected override ContextSnapshot CreateValidSnapshot()
    {
        byte[] pdf = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "linked-objstm.pdf"));
        return Snapshots.ForPaths(_dir.Write("a.pdf", pdf), _dir.Write("b.pdf", pdf));
    }

    public void Dispose() => _dir.Dispose();
}

public class InsertBlankPagesActionTests
{
    private readonly FileActionServices _services = new(null!, null!, new PowerTrove.Infrastructure.Files.FileSignatureVerifier());

    [Fact]
    public void Constructor_SetsDescriptorAndSelection()
    {
        var action = new InsertBlankPagesAction(_services);
        Assert.Equal("powertrove.pdf.insert-blank", action.Descriptor.Id);
        Assert.True(action.Descriptor.RequiresPreview);
        Assert.True(action.Descriptor.ChangesFiles);
    }
}

public class InsertBlankPagesActionBehaviorTests : PowerTrove.Abstractions.Testing.ActionContractTestsBase
{
    protected override IAction CreateAction()
    {
        var verifier = new PowerTrove.Infrastructure.Files.FileSignatureVerifier();
        var services = new FileActionServices(null!, null!, verifier);
        return new InsertBlankPagesAction(services);
    }

    protected override ContextSnapshot CreateValidSnapshot()
    {
        return new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(0, 0, null, DateTimeOffset.UtcNow, null),
            ContextStatus.Available,
            new FileSelection(new[] { new SelectedFile(null, "test.pdf", ".pdf", "/test.pdf", null, false, false, null, false) }),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "1.0",
            null,
            null);
}
}

public class WatermarkPdfActionBehaviorTests : PowerTrove.Abstractions.Testing.ActionContractTestsBase
{
    protected override IAction CreateAction()
    {
        var verifier = new PowerTrove.Infrastructure.Files.FileSignatureVerifier();
        var services = new FileActionServices(null!, null!, verifier);
        return new WatermarkPdfAction(services);
    }

    protected override ContextSnapshot CreateValidSnapshot()
    {
        return new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(0, 0, null, DateTimeOffset.UtcNow, null),
            ContextStatus.Available,
            new FileSelection(new[] { new SelectedFile(null, "test.pdf", ".pdf", "/test.pdf", null, false, false, null, false) }),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "1.0",
            null,
            null);
    }
}

public class PdfPackLocalizationTests
{
    [Fact]
    public void PdfSupport_MapsExceptionsToLocalizedMessages()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");

            var passwordEx = PdfSupport.Wrap(new FileKit.Pdf.PdfPasswordRequiredException());
            Assert.Equal("Das Passwort ist nicht korrekt.", passwordEx.Message);

            var encryptedEx = PdfSupport.Wrap(new FileKit.Pdf.PdfEncryptedException());
            Assert.Equal("Passwortgeschützte oder verschlüsselte PDFs werden noch nicht unterstützt.", encryptedEx.Message);

            var limitEx = PdfSupport.Wrap(new FileKit.Pdf.PdfLimitExceededException("Secret details"));
            Assert.Equal("Das PDF überschreitet die unterstützte Größe oder Komplexität.", limitEx.Message);

            var opEx = PdfSupport.Wrap(new FileKit.Pdf.PdfOperationException("Secret details"));
            Assert.Equal("Der PDF-Vorgang ist fehlgeschlagen.", opEx.Message);

            var damagedEx = PdfSupport.Wrap(new FileKit.Pdf.PdfException("Some other error."));
            Assert.Equal("Das PDF ist beschädigt und konnte nicht gelesen werden.", damagedEx.Message);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void AllPdfPackTexts_AreTranslatedToGerman()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            // explicit allowlist of proper names and format names
            var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PDF", "ZIP", "JSON", "CSV", "Markdown", "SQL", "MP3", "GIF", "JPEG", "PNG", "WebP", "Format", "Video",
                "A4", "A5", "Letter", "US Letter", "US Legal", "Legal", "Position", "n", "n / N", "2", "4", "90°", "180°", "270°", "Text", "Diagonal", "Horizontal", "Normal"
            };

            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
            var services = new FileActionServices(null!, null!, new PowerTrove.Infrastructure.Files.FileSignatureVerifier());
            var enPack = new PdfPack(services, null, null);
            var enActions = enPack.Actions.Select(a => a.Descriptor).ToList();

            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");
            var dePack = new PdfPack(services, null, null);
            var deActions = dePack.Actions.Select(a => a.Descriptor).ToList();

            Assert.Equal(enActions.Count, deActions.Count);

            for (int i = 0; i < enActions.Count; i++)
            {
                var enDesc = enActions[i];
                var deDesc = deActions[i];

                if (!allowedNames.Contains(enDesc.Title))
                    Assert.NotEqual(enDesc.Title, deDesc.Title);

                if (!allowedNames.Contains(enDesc.Description))
                    Assert.NotEqual(enDesc.Description, deDesc.Description);

                if (enDesc.Parameters != null)
                {
                    for (int p = 0; p < enDesc.Parameters.Count; p++)
                    {
                        var enParam = enDesc.Parameters[p];
                        var deParam = deDesc.Parameters[p];

                        if (!allowedNames.Contains(enParam.Title))
                            Assert.NotEqual(enParam.Title, deParam.Title);

                        if (enParam.Kind == ActionParameterKind.Choice && enParam.Choices != null)
                        {
                            for (int c = 0; c < enParam.Choices.Count; c++)
                            {
                                var enChoice = enParam.Choices[c];
                                var deChoice = deParam.Choices[c];

                                if (!allowedNames.Contains(enChoice.Title))
                                    Assert.NotEqual(enChoice.Title, deChoice.Title);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = originalCulture;
        }
    }
}
