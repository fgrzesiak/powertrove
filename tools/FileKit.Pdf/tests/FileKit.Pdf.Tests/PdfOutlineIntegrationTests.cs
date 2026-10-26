using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace FileKit.Pdf.Tests;

public class PdfOutlineIntegrationTests
{
    private PdfDocument LoadFixture()
    {
        var path = Path.Combine("Fixtures", "linked-objstm.pdf");
        if (!File.Exists(path)) {
            path = Path.Combine("..", "..", "..", "..", "Fixtures", "linked-objstm.pdf");
        }
        if (!File.Exists(path)) {
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../tests/FileKit.Pdf.Tests/Fixtures/linked-objstm.pdf");
        }
        return PdfDocument.Load(File.ReadAllBytes(path));
    }

    [Fact]
    public void Builder_SetOutline_RoundTripsCorrectly()
    {
        var doc = LoadFixture();
        var outlines = PdfOutline.Read(doc);

        Assert.Equal(3, outlines.Count); // From existing tests: First, Second, Third

        using var ms = new MemoryStream();
        var builder = new PdfDocumentBuilder();
        for (int i = 0; i < doc.PageCount; i++)
        {
            builder.AddPage(doc, i);
        }

        // Add a fake bookmark structure with UTF16
        var newOutlines = new List<PdfOutlineItem>
        {
            new("ASCII Bookmark", 0, new List<PdfOutlineItem>
            {
                new("UTF-16 \u00E4\u00F6\u00FC", 1, Array.Empty<PdfOutlineItem>())
            })
        };

        builder.SetOutline(newOutlines);
        builder.Save(ms);

        var rewritten = PdfDocument.Load(ms.ToArray());
        var readOutlines = PdfOutline.Read(rewritten);

        Assert.Single(readOutlines);
        Assert.Equal("ASCII Bookmark", readOutlines[0].Title);
        Assert.Equal(0, readOutlines[0].PageIndex);

        Assert.Single(readOutlines[0].Children);
        Assert.Equal("UTF-16 \u00E4\u00F6\u00FC", readOutlines[0].Children[0].Title);
        Assert.Equal(1, readOutlines[0].Children[0].PageIndex);
    }

    [Fact]
    public void ExtractPages_KeepsSurvivingBookmarks()
    {
        var doc = LoadFixture();
        using var ms = new MemoryStream();
        PdfTools.ExtractPages(doc, new[] { 0, 2 }, ms); // Keep First and Third page

        var rewritten = PdfDocument.Load(ms.ToArray());
        var outlines = PdfOutline.Read(rewritten);

        Assert.Equal(2, outlines.Count);
        Assert.Equal("First", outlines[0].Title);
        Assert.Equal(0, outlines[0].PageIndex);

        Assert.Equal("Third", outlines[1].Title);
        Assert.Equal(1, outlines[1].PageIndex); // Third page is now at index 1
    }

    [Fact]
    public void RemovePages_KeepsSurvivingBookmarks()
    {
        var doc = LoadFixture();
        using var ms = new MemoryStream();
        PdfTools.RemovePages(doc, new[] { 0 }, ms); // Remove First page

        var rewritten = PdfDocument.Load(ms.ToArray());
        var outlines = PdfOutline.Read(rewritten);

        Assert.Equal(2, outlines.Count);
        Assert.Equal("Second", outlines[0].Title);
        Assert.Equal(0, outlines[0].PageIndex);

        Assert.Equal("Third", outlines[1].Title);
        Assert.Equal(1, outlines[1].PageIndex);
    }

    [Fact]
    public void Reorder_MapsToFirstOccurrence()
    {
        var doc = LoadFixture();
        using var ms = new MemoryStream();
        PdfTools.Reorder(doc, new[] { 2, 1, 0 }, ms); // Reverse order

        var rewritten = PdfDocument.Load(ms.ToArray());
        var outlines = PdfOutline.Read(rewritten);

        Assert.Equal(3, outlines.Count);
        Assert.Equal("First", outlines[0].Title);
        Assert.Equal(2, outlines[0].PageIndex); // First page is now at index 2

        Assert.Equal("Second", outlines[1].Title);
        Assert.Equal(1, outlines[1].PageIndex); // Second page is still at index 1

        Assert.Equal("Third", outlines[2].Title);
        Assert.Equal(0, outlines[2].PageIndex); // Third page is now at index 0
    }

    [Fact]
    public void Rotate_KeepsBookmarksAndIdentityMapping()
    {
        var doc = LoadFixture();
        using var ms = new MemoryStream();
        PdfTools.Rotate(doc, 90, null, ms);

        var rewritten = PdfDocument.Load(ms.ToArray());
        var outlines = PdfOutline.Read(rewritten);

        Assert.Equal(3, outlines.Count);
        Assert.Equal("First", outlines[0].Title);
        Assert.Equal(0, outlines[0].PageIndex);
        Assert.Equal("Third", outlines[2].Title);
        Assert.Equal(2, outlines[2].PageIndex);
    }
}
