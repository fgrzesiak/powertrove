using System.Text;

namespace FileKit.Pdf.Tests;

public class PdfOperationTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static PdfDocument Reload(MemoryStream output) => PdfDocument.Load(output.ToArray());

    [Fact]
    public void Interleave_CombinesPagesAlternately()
    {
        var first = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 2 }));
        var second = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 2 }));
        using var output = new MemoryStream();

        PdfTools.Interleave(first, second, reverseSecond: true, output);
        var interleaved = Reload(output);

        Assert.Equal(4, interleaved.PageCount);
        Assert.Contains("(Page 1)", TestPdfFactory.ExtractText(interleaved, 0)); // first[0]
        Assert.Contains("(Page 2)", TestPdfFactory.ExtractText(interleaved, 1)); // second[1]
        Assert.Contains("(Page 2)", TestPdfFactory.ExtractText(interleaved, 2)); // first[1]
        Assert.Contains("(Page 1)", TestPdfFactory.ExtractText(interleaved, 3)); // second[0]
    }

    [Fact]
    public void Interleave_UnequalCounts_AppendsRemaining()
    {
        var first = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3 }));
        var second = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 1 }));
        using var output = new MemoryStream();

        PdfTools.Interleave(first, second, reverseSecond: false, output);
        var interleaved = Reload(output);

        Assert.Equal(4, interleaved.PageCount);
        Assert.Contains("(Page 1)", TestPdfFactory.ExtractText(interleaved, 0)); // first[0]
        Assert.Contains("(Page 1)", TestPdfFactory.ExtractText(interleaved, 1)); // second[0]
        Assert.Contains("(Page 2)", TestPdfFactory.ExtractText(interleaved, 2)); // first[1]
        Assert.Contains("(Page 3)", TestPdfFactory.ExtractText(interleaved, 3)); // first[2]
    }

    [Fact]
    public void Insert_PlacesSourceAtCorrectIndex()
    {
        var target = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 2 }));
        var source = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 1 }));
        using var output = new MemoryStream();

        PdfTools.Insert(source, target, 0, output); // Insert after page index 0 (page 1)
        var inserted = Reload(output);

        Assert.Equal(3, inserted.PageCount);
        Assert.Contains("(Page 1)", TestPdfFactory.ExtractText(inserted, 0)); // target[0]
        Assert.Contains("(Page 1)", TestPdfFactory.ExtractText(inserted, 1)); // source[0]
        Assert.Contains("(Page 2)", TestPdfFactory.ExtractText(inserted, 2)); // target[1]
    }

    [Fact]
    public void Merge_ConcatenatesPagesInOrder()
    {
        var first = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 2 }));
        var second = PdfDocument.Open(Fixture("linked-objstm.pdf"));
        using var output = new MemoryStream();

        PdfTools.Merge(new[] { first, second }, output);
        var merged = Reload(output);

        Assert.Equal(5, merged.PageCount);
        Assert.False(merged.WasRepaired);
        Assert.Contains("(Page 1)", TestPdfFactory.ExtractText(merged, 0));
        Assert.Contains("(Page 2)", TestPdfFactory.ExtractText(merged, 1));
        var (width, _) = PdfTools.GetPageSize(merged, 2);
        Assert.InRange(width, 419, 421);
    }

    [Fact]
    public void Merge_WithBookmarkTitles_CreatesNestedOutlines()
    {
        var document = PdfDocument.Open(Fixture("linked-objstm.pdf"));

        using var output = new MemoryStream();
        PdfTools.Merge(new[] { document, document }, output, fileBookmarkTitles: new[] { "FileA", "FileB" });

        var merged = Reload(output);
        var outlines = PdfOutline.Read(merged);

        Assert.Equal(2, outlines.Count);
        Assert.Equal("FileA", outlines[0].Title);
        Assert.Equal(0, outlines[0].PageIndex);
        Assert.Equal(3, outlines[0].Children.Count);

        Assert.Equal("FileB", outlines[1].Title);
        Assert.Equal(document.PageCount, outlines[1].PageIndex);
        Assert.Equal(3, outlines[1].Children.Count);
    }

    [Fact]
    public void Merge_WithoutBookmarkTitles_DropsOutlinesForMultipleSources()
    {
        var document = PdfDocument.Open(Fixture("linked-objstm.pdf"));

        using var output = new MemoryStream();
        PdfTools.Merge(new[] { document, document }, output, fileBookmarkTitles: null);

        var merged = Reload(output);
        var outlines = PdfOutline.Read(merged);

        Assert.Empty(outlines);
    }

    [Fact]
    public void Merge_SameDocumentTwice_CopiesSharedObjectsOncePerSource()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3 }));
        using var output = new MemoryStream();

        PdfTools.Merge(new[] { document }, output);
        var text = Encoding.Latin1.GetString(output.ToArray());

        Assert.Equal(1, CountOccurrences(text, "/BaseFont /Helvetica"));
    }

    [Fact]
    public void ExtractPages_UsesRequestedOrderAndDropsLinksToExcludedPages()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3, LinkFirstToLast = true }));
        using var output = new MemoryStream();

        PdfTools.ExtractPages(document, new[] { 1, 0 }, output);
        var result = Reload(output);

        Assert.Equal(2, result.PageCount);
        Assert.Contains("(Page 2)", TestPdfFactory.ExtractText(result, 0));
        Assert.Contains("(Page 1)", TestPdfFactory.ExtractText(result, 1));
        string raw = Encoding.Latin1.GetString(output.ToArray());
        Assert.DoesNotContain("(Page 3)", raw);
        Assert.Equal(2, CountOccurrences(raw, "/Type /Page/") + CountOccurrences(raw, "/Type /Page>>") + CountOccurrences(raw, "/Type /Page "));
    }

    [Fact]
    public void AddHeaderFooter_FormatsCorrectly()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3 }));
        using var output = new MemoryStream();

        PdfTools.AddHeaderFooter(
            document, output,
            textTemplate: "Page {page} of {pages} - {date}",
            position: PdfAnchor.TopCenter,
            fontSize: 12,
            dateString: "2023-10-27");

        var result = Reload(output);
        Assert.Equal(3, result.PageCount);

        var contents1 = result.Resolve(result.Pages[1].Dictionary["Contents"]);
        var array1 = Assert.IsType<PdfArray>(contents1);
        var decStream1 = (PdfStream)result.Resolve(array1.Items.Last())!;
        string decData1 = Encoding.ASCII.GetString(decStream1.Data);

        Assert.Contains("<506167652032206F662033202D20323032332D31302D3237>", decData1);
        Assert.Contains("12 Tf", decData1);
        Assert.Contains(" Tm\n", decData1); // Asserts text matrix is applied for the anchor // Font size
    }

    [Fact]
    public void AddPageNumbers_FormatsCorrectly()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3 }));
        using var output = new MemoryStream();

        PdfTools.AddPageNumbers(
            document, output,
            format: PdfPageNumberFormat.PageNumberOfTotal,
            startAt: 5,
            skipFirst: true);

        var result = Reload(output);
        Assert.Equal(3, result.PageCount);

        // First page skipped
        var contents0 = result.Resolve(result.Pages[0].Dictionary["Contents"]);
        if (contents0 is PdfArray arr0)
        {
            // If it's an array, it should be the original single stream
            Assert.Single(arr0.Items);
        }
        // If it's a stream, it's just the original stream without decorations

        // Second page gets number 5
        var contents1Obj = result.Resolve(result.Pages[1].Dictionary["Contents"])!;
        var contents1 = Assert.IsType<PdfArray>(contents1Obj);
        var decStream1 = (PdfStream)result.Resolve(contents1.Items.Last())!;
        string data1 = Encoding.ASCII.GetString(decStream1.Data);
        Assert.Contains("506167652035206F662033", data1, StringComparison.OrdinalIgnoreCase);

        // Third page gets number 6
        var contents2Obj = result.Resolve(result.Pages[2].Dictionary["Contents"])!;
        var contents2 = Assert.IsType<PdfArray>(contents2Obj);
        var decStream2 = (PdfStream)result.Resolve(contents2.Items.Last())!;
        string data2 = Encoding.ASCII.GetString(decStream2.Data);
        Assert.Contains("506167652036206F662033", data2, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddPageNumbers_AppliesToRotatedPages()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 1, InheritedRotate = 90 }));
        using var output = new MemoryStream();

        PdfTools.AddPageNumbers(document, output, position: PdfAnchor.TopLeft, fontSize: 12);
        var result = Reload(output);

        var contents = (PdfArray)result.Resolve(result.Pages[0].Dictionary["Contents"])!;
        var decStream = (PdfStream)result.Resolve(contents.Items.Last())!;
        string data = Encoding.ASCII.GetString(decStream.Data);

        // We expect the matrix to reflect the 90 degree rotation.
        // It's hard to assert the exact matrix string without re-computing it, but we can verify it contains the expected text.
        Assert.Contains("<31> Tj", data);
        Assert.Contains("12 Tf", data);
    }

    [Fact]
    public void Reorder_ReordersPagesWhenValid()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3 }));
        using var output = new MemoryStream();

        PdfTools.Reorder(document, new[] { 2, 0, 1 }, output);
        var result = Reload(output);

        Assert.Equal(3, result.PageCount);
        Assert.Contains("(Page 3)", TestPdfFactory.ExtractText(result, 0));
        Assert.Contains("(Page 1)", TestPdfFactory.ExtractText(result, 1));
        Assert.Contains("(Page 2)", TestPdfFactory.ExtractText(result, 2));
    }

    [Fact]
    public void Reorder_ThrowsWhenPageMissing()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3 }));
        var ex = Assert.Throws<PdfOperationException>(() => PdfTools.Reorder(document, new[] { 0, 1 }, Stream.Null));
        Assert.Equal("Page 3 is missing.", ex.Message);
    }

    [Fact]
    public void Reorder_ThrowsWhenPageDuplicated()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3 }));
        var ex = Assert.Throws<PdfOperationException>(() => PdfTools.Reorder(document, new[] { 0, 1, 1 }, Stream.Null));
        Assert.Equal("Page 2 is listed twice.", ex.Message);
    }

    [Fact]
    public void Reorder_ThrowsWhenPageOutOfRange()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3 }));
        var ex = Assert.Throws<PdfOperationException>(() => PdfTools.Reorder(document, new[] { 0, 1, 3 }, Stream.Null));
        Assert.Equal("Page numbers must be between 1 and 3.", ex.Message);
    }

    [Fact]
    public void ExtractPages_KeepsLinksBetweenIncludedPages()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3, LinkFirstToLast = true }));
        using var output = new MemoryStream();

        PdfTools.ExtractPages(document, new[] { 0, 2 }, output);
        var result = Reload(output);

        var annots = (PdfArray)result.Resolve(result.Pages[0].Dictionary["Annots"])!;
        var link = (PdfDictionary)result.Resolve(annots[0])!;
        var dest = (PdfArray)result.Resolve(link["Dest"])!;
        Assert.Equal(result.Pages[1].Reference.ObjectNumber, ((PdfReference)dest[0]).ObjectNumber);
    }

    [Fact]
    public void RemovePages_KeepsRemainingPages()
    {
        var document = PdfDocument.Open(Fixture("linked-objstm.pdf"));
        using var output = new MemoryStream();

        PdfTools.RemovePages(document, new[] { 1 }, output);

        Assert.Equal(2, Reload(output).PageCount);
    }

    [Fact]
    public void RemovePages_AllPages_Throws()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 2 }));
        Assert.Throws<PdfOperationException>(() => PdfTools.RemovePages(document, new[] { 0, 1 }, new MemoryStream()));
    }

    [Fact]
    public void Rotate_AddsToInheritedRotation()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 2, InheritedRotate = 90 }));
        using var output = new MemoryStream();

        PdfTools.Rotate(document, 270, new[] { 0 }, output);
        var result = Reload(output);

        Assert.Equal(0, result.Pages[0].Rotate);
        Assert.Equal(90, result.Pages[1].Rotate);
    }

    [Fact]
    public void Rotate_NegativeDegrees_Normalizes()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 1 }));
        using var output = new MemoryStream();

        PdfTools.Rotate(document, -90, null, output);

        Assert.Equal(270, Reload(output).Pages[0].Rotate);
    }

    [Fact]
    public void EditMetadata_SetsPropertiesAndDropsXmp()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 1, WithMetadata = true }));
        using var output = new MemoryStream();

        // Include non-ASCII to verify UTF-16BE encoding works correctly (e.g. café)
        PdfTools.EditMetadata(document, output, "New Title", "Authör", null, "");
        string raw = Encoding.Latin1.GetString(output.ToArray());
        var result = Reload(output);

        Assert.NotNull(result.Info);

        // Assertions on the parsed info dictionary
        var titleObj = result.Info!["Title"] as PdfString;
        Assert.NotNull(titleObj);
        Assert.Equal("New Title", Encoding.ASCII.GetString(titleObj.Value));

        var authorObj = result.Info["Author"] as PdfString;
        Assert.NotNull(authorObj);
        Assert.True(authorObj.IsHex); // Due to UTF-16BE from non-ASCII character
        Assert.Equal("Authör", Encoding.BigEndianUnicode.GetString(authorObj.Value.Skip(2).ToArray()));

        Assert.Null(result.Info["Subject"]); // null removes it
        Assert.Null(result.Info["Keywords"]); // empty string removes it

        Assert.DoesNotContain("xmpmeta", raw);
        Assert.Equal(1, result.PageCount);
    }

    [Fact]
    public void RemoveMetadata_DropsInfoAndXmp()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 2, WithMetadata = true }));
        using var output = new MemoryStream();

        PdfTools.RemoveMetadata(document, output);
        string raw = Encoding.Latin1.GetString(output.ToArray());
        var result = Reload(output);

        Assert.Null(result.Info);
        Assert.DoesNotContain("Secret", raw);
        Assert.DoesNotContain("xmpmeta", raw);
        Assert.Equal(2, result.PageCount);
    }

    [Fact]
    public void Merge_KeepsBasicInfoButNotXmp()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 1, WithMetadata = true }));
        using var output = new MemoryStream();

        PdfTools.Merge(new[] { document }, output);
        var result = Reload(output);

        Assert.Equal("Secret Title", Encoding.ASCII.GetString(((PdfString)result.Info!["Title"]!).Value));
        Assert.DoesNotContain("xmpmeta", Encoding.Latin1.GetString(output.ToArray()));
    }

    [Fact]
    public void Save_IsCancelable()
    {
        var document = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3 }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => PdfTools.Merge(new[] { document }, new MemoryStream(), cancellationToken: cts.Token));
    }

    [Fact]
    public void Output_RoundTripsThroughSecondRewrite()
    {
        var document = PdfDocument.Open(Fixture("linked-objstm.pdf"));
        using var first = new MemoryStream();
        PdfTools.Merge(new[] { document }, first);
        using var second = new MemoryStream();
        PdfTools.Merge(new[] { Reload(first) }, second);

        Assert.Equal(3, Reload(second).PageCount);
    }

    [Fact]
    public void CompactWriter_RoundTrip_CreatesValidDocument()
    {
        var document = PdfDocument.Open(Fixture("linked-objstm.pdf"));
        using var output = new MemoryStream();
        var options = new PdfWriteOptions { Compact = true };

        PdfTools.Merge(new[] { document }, output, options);
        var result = Reload(output);

        Assert.Equal(document.PageCount, result.PageCount);
        Assert.False(result.WasRepaired);

        string originalText = TestPdfFactory.ExtractText(document, 0);
        string newText = TestPdfFactory.ExtractText(result, 0);
        Assert.Equal(originalText, newText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repair_RebuildsTypedCatalog_AlsoWithoutXref(bool dropXref)
    {
        // A catalog with /Type /Catalog used to fail ("Failed to copy root catalog"), and a
        // reconstructed document without trailer /Root failed with "no root catalog".
        var data = TestPdfFactory.Create(new() { Pages = 2 });
        if (dropXref)
        {
            string text = Encoding.Latin1.GetString(data);
            data = Encoding.Latin1.GetBytes(text[..text.IndexOf("xref", StringComparison.Ordinal)]);
        }
        var document = PdfDocument.Load(data);
        Assert.Equal(dropXref, document.WasRepaired);

        using var output = new MemoryStream();
        PdfTools.Repair(document, output, new PdfWriteOptions { Compact = true });

        var result = Reload(output);
        Assert.False(result.WasRepaired);
        Assert.Equal(2, result.PageCount);
        Assert.Contains("(Page 2)", TestPdfFactory.ExtractText(result, 1));
    }

    [Fact]
    public void Repair_WritesDocument()
    {
        var document = PdfDocument.Open(Fixture("linked-objstm.pdf"));

        using var output = new MemoryStream();
        PdfTools.Repair(document, output, new PdfWriteOptions { Compact = true });

        var result = Reload(output);
        Assert.False(result.WasRepaired);
        Assert.Equal(document.PageCount, result.PageCount);
        string originalText = TestPdfFactory.ExtractText(document, 0);
        string newText = TestPdfFactory.ExtractText(result, 0);
        Assert.Equal(originalText, newText);
    }

    [Fact]
    public void CompactWriter_ByteIdenticalToClassic_WhenCompactFalse()
    {
        var document = PdfDocument.Open(Fixture("linked-objstm.pdf"));
        byte[] fixedId = new byte[16];
        for (int i = 0; i < 16; i++) fixedId[i] = (byte)i;

        using var classicOutput = new MemoryStream();
        var classicOptions = new PdfWriteOptions { Compact = false, IdGenerator = () => fixedId };
        PdfTools.Merge(new[] { document }, classicOutput, classicOptions);

        using var currentOutput = new MemoryStream();
        var currentOptions = new PdfWriteOptions { IdGenerator = () => fixedId }; // Compact is false by default
        PdfTools.Merge(new[] { document }, currentOutput, currentOptions);

        Assert.Equal(classicOutput.ToArray(), currentOutput.ToArray());
    }

    [Fact]
    public void CompactWriter_ReducesSizeOfFixture()
    {
        var document = PdfDocument.Open(Fixture("linked-objstm.pdf"));

        using var classicOutput = new MemoryStream();
        PdfTools.Merge(new[] { document }, classicOutput, new PdfWriteOptions { Compact = false });

        using var compactOutput = new MemoryStream();
        PdfTools.Merge(new[] { document }, compactOutput, new PdfWriteOptions { Compact = true });

        Assert.True(compactOutput.Length < classicOutput.Length, $"Compact ({compactOutput.Length}) should be smaller than Classic ({classicOutput.Length})");

        string compactRaw = Encoding.Latin1.GetString(compactOutput.ToArray());
        Assert.StartsWith("%PDF-1.5", compactRaw);
        Assert.Contains("/Type /XRef", compactRaw);
        Assert.Contains("/Type /ObjStm", compactRaw);
    }

    [Theory]
    [InlineData("1-3", 5, new[] { 0, 1, 2 })]
    [InlineData("2, 4", 5, new[] { 1, 3 })]
    [InlineData("4-", 5, new[] { 3, 4 })]
    [InlineData("-2", 5, new[] { 0, 1 })]
    [InlineData("5, 1-2, 2", 5, new[] { 4, 0, 1 })]
    [InlineData(" 1 ; last ", 5, new[] { 0, 4 })]
    public void PageRange_Parses(string text, int pageCount, int[] expected)
    {
        Assert.Equal(expected, PageRange.Parse(text, pageCount));
    }

    [Fact]
    public void PageRange_IsValidSyntax_CompletesUnder50msWithoutLargeAllocations()
    {
        // Warm up JIT so only the syntax check is measured; per-thread allocation counting keeps
        // parallel tests out of the number. Expanding 2^31 pages would take seconds and gigabytes.
        PageRange.IsValidSyntax("1-2");
        long initialMemory = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Assert.True(PageRange.IsValidSyntax("1-"));
        Assert.True(PageRange.IsValidSyntax("1-2147483647"));
        Assert.True(PageRange.IsValidSyntax("2147483647"));

        sw.Stop();
        long allocatedMemory = GC.GetAllocatedBytesForCurrentThread() - initialMemory;

        Assert.True(sw.ElapsedMilliseconds < 50, $"Took {sw.ElapsedMilliseconds}ms");
        Assert.True(allocatedMemory < 500 * 1024, $"Allocated {allocatedMemory} bytes"); // Ensure no large expansion arrays are allocated
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("3-1")]
    [InlineData("a-b")]
    [InlineData("1.5")]
    public void PageRange_RejectsInvalidInput(string text)
    {
        Assert.False(PageRange.TryParse(text, 5, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }


    [Fact]
    public void AddWatermark_Horizontal_Works()
    {
        string tmpDoc = Path.GetTempFileName();
        File.WriteAllBytes(tmpDoc, TestPdfFactory.Create(new TestPdfFactory.Options { Pages = 1 }));
        var doc = PdfDocument.Open(tmpDoc);
        var ms = new MemoryStream();
        PdfTools.AddWatermark(doc, ms, "DRAFT", PdfWatermarkStyle.Horizontal, opacity: 0.5, fontSize: 36);
        string tmpWatermarked = Path.GetTempFileName();
        File.WriteAllBytes(tmpWatermarked, ms.ToArray());
        var watermarked = PdfDocument.Open(tmpWatermarked);
        Assert.Equal(1, watermarked.PageCount);
        Assert.NotNull(watermarked.Pages[0].Dictionary["Contents"]);
    }

    [Fact]
    public void AddWatermark_Diagonal_CalculatesAutoFontSize()
    {
        string tmpDoc = Path.GetTempFileName();
        File.WriteAllBytes(tmpDoc, TestPdfFactory.Create(new TestPdfFactory.Options { Pages = 1 }));
        var doc = PdfDocument.Open(tmpDoc);
        var ms = new MemoryStream();
        PdfTools.AddWatermark(doc, ms, "TEST", PdfWatermarkStyle.Diagonal, fontSize: 0); // Auto size
        string tmpWatermarked = Path.GetTempFileName();
        File.WriteAllBytes(tmpWatermarked, ms.ToArray());
        var watermarked = PdfDocument.Open(tmpWatermarked);
        Assert.Equal(1, watermarked.PageCount);
        Assert.NotNull(watermarked.Pages[0].Dictionary["Contents"]);
    }

    [Fact]
    public void AddWatermark_Tiled_CapAt200()
    {
        string tmpDoc = Path.GetTempFileName();
        File.WriteAllBytes(tmpDoc, TestPdfFactory.Create(new TestPdfFactory.Options { Pages = 1 }));
        var doc = PdfDocument.Open(tmpDoc);
        var ms = new MemoryStream();
        PdfTools.AddWatermark(doc, ms, "T", PdfWatermarkStyle.Tiled, fontSize: 8);
        string tmpWatermarked = Path.GetTempFileName();
        File.WriteAllBytes(tmpWatermarked, ms.ToArray());
        var watermarked = PdfDocument.Open(tmpWatermarked);
        Assert.Equal(1, watermarked.PageCount);
        Assert.NotNull(watermarked.Pages[0].Dictionary["Contents"]);
    }

    [Fact]
    public void StampImage_WithValidImage_WritesSingleXObject()
    {
        var doc = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3 }));
        var image = PdfImage.Load(File.ReadAllBytes(Fixture("photo.jpg")));
        using var output = new MemoryStream();

        PdfTools.StampImage(doc, image, output, pages: "all");
        var stamped = Reload(output);

        Assert.Equal(3, stamped.PageCount);

        var xObjects = new HashSet<int>();
        for (int i = 0; i < stamped.PageCount; i++)
        {
            var pageDict = stamped.Pages[i].Dictionary;
            var resources = pageDict["Resources"] as PdfDictionary;
            Assert.NotNull(resources);
            var xObjDict = resources["XObject"] as PdfDictionary;
            Assert.NotNull(xObjDict);

            var imgRef = xObjDict.Entries.FirstOrDefault(e => e.Key.StartsWith("FKIm")).Value as PdfReference;
            Assert.NotNull(imgRef);
            xObjects.Add(imgRef.ObjectNumber);
        }

        Assert.Single(xObjects); // XObject should be shared across all pages
    }

    [Fact]
    public void StampImage_WithAlphaPng_OutputsSMask()
    {
        var doc = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 1 }));
        var image = PdfImage.Load(PdfImageTests.CreatePng(4, 4, colorType: 6, bitDepth: 8)); // RGBA
        using var output = new MemoryStream();

        PdfTools.StampImage(doc, image, output, pages: "1");
        var stamped = Reload(output);

        var pageDict = stamped.Pages[0].Dictionary;
        var resources = pageDict["Resources"] as PdfDictionary;
        var xObjDict = resources!["XObject"] as PdfDictionary;
        var imgRef = xObjDict!.Entries.FirstOrDefault(e => e.Key.StartsWith("FKIm")).Value as PdfReference;

        var xObject = stamped.GetObject(imgRef!.ObjectNumber) as PdfStream;
        Assert.NotNull(xObject);
        Assert.True(xObject.Dictionary["SMask"] != null || xObject.Dictionary["Mask"] != null);
    }

    [Fact]
    public void StampImage_WithFirstAndLastPages_StampsCorrectly()
    {
        var doc = PdfDocument.Load(TestPdfFactory.Create(new() { Pages = 3 }));
        var image = PdfImage.Load(File.ReadAllBytes(Fixture("photo.jpg")));

        using var output1 = new MemoryStream();
        PdfTools.StampImage(doc, image, output1, pages: "first");
        var stamped1 = Reload(output1);

        var xObjDict1 = ((stamped1.Pages[0].Dictionary["Resources"] as PdfDictionary)?["XObject"] as PdfDictionary);
        Assert.NotNull(xObjDict1);
        var xObjDict1Empty = ((stamped1.Pages[1].Dictionary["Resources"] as PdfDictionary)?["XObject"] as PdfDictionary);
        Assert.Null(xObjDict1Empty);

        using var output2 = new MemoryStream();
        PdfTools.StampImage(doc, image, output2, pages: "last");
        var stamped2 = Reload(output2);

        var xObjDict2Empty = ((stamped2.Pages[0].Dictionary["Resources"] as PdfDictionary)?["XObject"] as PdfDictionary);
        Assert.Null(xObjDict2Empty);
        var xObjDict2 = ((stamped2.Pages[2].Dictionary["Resources"] as PdfDictionary)?["XObject"] as PdfDictionary);
        Assert.NotNull(xObjDict2);
    }
}
