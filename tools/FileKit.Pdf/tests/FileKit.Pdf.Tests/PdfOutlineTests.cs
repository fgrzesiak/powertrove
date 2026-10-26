using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using Xunit;
using FileKit.Pdf;

namespace FileKit.Pdf.Tests;

public class PdfOutlineTests
{
    [Fact]
    public void Read_LinkedObjStm_ReturnsCorrectTree()
    {
        var path = Path.Combine("Fixtures", "linked-objstm.pdf");
        if (!File.Exists(path)) {
            path = Path.Combine("..", "..", "..", "..", "Fixtures", "linked-objstm.pdf");
        }
        if (!File.Exists(path)) {
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../tests/FileKit.Pdf.Tests/Fixtures/linked-objstm.pdf");
        }

        var doc = PdfDocument.Open(path);
        var outline = PdfOutline.Read(doc);

        Assert.NotNull(outline);
        Assert.Equal(3, outline.Count);

        Assert.Equal("First", outline[0].Title);
        Assert.Equal(0, outline[0].PageIndex);
        Assert.Empty(outline[0].Children); // the fixture has flattened children or no children in First, actually First, Second, Third

        Assert.Equal("Second", outline[1].Title);
        Assert.Equal(1, outline[1].PageIndex);
        Assert.Empty(outline[1].Children);

        Assert.Equal("Third", outline[2].Title);
        Assert.Equal(2, outline[2].PageIndex);
        Assert.Empty(outline[2].Children);
    }


    [Fact]
    public void Read_ExplicitArrayDest_ReturnsCorrectPageIndex()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Outlines /First 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Title (Explicit) /Dest [4 0 R /Fit] >>"
        };
        byte[] pdf = TestPdfFactory.Serialize(objects, "/Root 1 0 R");
        var doc = PdfDocument.Load(pdf);
        var outline = PdfOutline.Read(doc);
        Assert.Single(outline);
        Assert.Equal("Explicit", outline[0].Title);
        Assert.Equal(0, outline[0].PageIndex);
    }

    [Fact]
    public void Read_ExplicitArrayDest_IntegerPage_ReturnsCorrectPageIndex()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Outlines /First 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Title (Explicit) /Dest [0 /Fit] >>"
        };
        byte[] pdf = TestPdfFactory.Serialize(objects, "/Root 1 0 R");
        var doc = PdfDocument.Load(pdf);
        var outline = PdfOutline.Read(doc);
        Assert.Single(outline);
        Assert.Equal("Explicit", outline[0].Title);
        Assert.Equal(0, outline[0].PageIndex);
    }

    [Fact]
    public void Read_DestsDictionary_ReturnsCorrectPageIndex()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R /Dests << /MyDest [4 0 R /Fit] >> >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Outlines /First 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Title (DictDest) /Dest /MyDest >>"
        };
        byte[] pdf = TestPdfFactory.Serialize(objects, "/Root 1 0 R");
        var doc = PdfDocument.Load(pdf);
        var outline = PdfOutline.Read(doc);
        Assert.Single(outline);
        Assert.Equal("DictDest", outline[0].Title);
        Assert.Equal(0, outline[0].PageIndex);
    }

    [Fact]
    public void Read_NamesDestsNameTree_ReturnsCorrectPageIndex()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R /Names << /Dests << /Kids [6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Outlines /First 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Title (TreeDest) /Dest /MyTreeDest >>",
            "<< /Names [(MyTreeDest) << /D [4 0 R /Fit] >>] >>"
        };
        byte[] pdf = TestPdfFactory.Serialize(objects, "/Root 1 0 R");
        var doc = PdfDocument.Load(pdf);
        var outline = PdfOutline.Read(doc);
        Assert.Single(outline);
        Assert.Equal("TreeDest", outline[0].Title);
        Assert.Equal(0, outline[0].PageIndex);
    }

    [Fact]
    public void Read_GoToAction_ReturnsCorrectPageIndex()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Outlines /First 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Title (ActionDest) /A << /S /GoTo /D [4 0 R /Fit] >> >>"
        };
        byte[] pdf = TestPdfFactory.Serialize(objects, "/Root 1 0 R");
        var doc = PdfDocument.Load(pdf);
        var outline = PdfOutline.Read(doc);
        Assert.Single(outline);
        Assert.Equal("ActionDest", outline[0].Title);
        Assert.Equal(0, outline[0].PageIndex);
    }

    [Fact]
    public void Read_Utf16BETitle_ReturnsDecodedString()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Outlines /First 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Title <FEFF005500540046> /Dest [4 0 R /Fit] >>"
        };
        byte[] pdf = TestPdfFactory.Serialize(objects, "/Root 1 0 R");
        var doc = PdfDocument.Load(pdf);
        var outline = PdfOutline.Read(doc);
        Assert.Single(outline);
        Assert.Equal("UTF", outline[0].Title);
    }

    [Fact]
    public void Read_UnresolvableDest_ReturnsNullPageIndex()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Outlines /First 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Title (MissingDest) /Dest /DoesNotExist >>"
        };
        byte[] pdf = TestPdfFactory.Serialize(objects, "/Root 1 0 R");
        var doc = PdfDocument.Load(pdf);
        var outline = PdfOutline.Read(doc);
        Assert.Single(outline);
        Assert.Equal("MissingDest", outline[0].Title);
        Assert.Null(outline[0].PageIndex);
    }



    [Fact]
    public void Read_NextCycle_TerminatesEarly()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Outlines /First 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Title (Cycle1) /Next 6 0 R >>",
            "<< /Title (Cycle2) /Next 5 0 R >>"
        };
        byte[] pdf = TestPdfFactory.Serialize(objects, "/Root 1 0 R");
        var doc = PdfDocument.Load(pdf);
        var outline = PdfOutline.Read(doc);
        Assert.Equal(2, outline.Count);
        Assert.Equal("Cycle1", outline[0].Title);
        Assert.Equal("Cycle2", outline[1].Title);
    }

    [Fact]
    public void Read_FirstCycle_TerminatesEarly()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Outlines /First 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Title (CycleRoot) /First 5 0 R >>"
        };
        byte[] pdf = TestPdfFactory.Serialize(objects, "/Root 1 0 R");
        var doc = PdfDocument.Load(pdf);
        var outline = PdfOutline.Read(doc);
        Assert.Single(outline);
        Assert.Equal("CycleRoot", outline[0].Title);
        Assert.Empty(outline[0].Children);
    }

    [Fact]
    public void Read_DeepNesting_LimitsTo50()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Outlines /First 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>"
        };
        for (int i = 0; i < 60; i++)
        {
            objects.Add($"<< /Title (Level{i}) /First {i + 6} 0 R >>");
        }
        objects.Add($"<< /Title (Deepest) >>");

        byte[] pdf = TestPdfFactory.Serialize(objects, "/Root 1 0 R");
        var doc = PdfDocument.Load(pdf);
        var outline = PdfOutline.Read(doc);

        Assert.Single(outline);

        var current = outline[0];
        int depth = 0;
        while (current.Children.Count > 0)
        {
            depth++;
            current = current.Children[0];
        }

        Assert.True(depth <= 50);
    }

    [Fact]
    public void Read_ManyItems_LimitsTo10000()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Outlines /First 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>"
        };

        // This test would require generating 10000 items. To make it fast, we can use a cycle and check the total item count is bounded.
        // We already have cycle testing, so we can just rely on the cycle test. Wait, a Next cycle stops early due to cycle detection.
        // To test the 10,000 item limit, we need 10,000 non-cyclic items.
        // We can create an array of 10,005 items inline if we don't use objects. Wait, outlines use a linked list, so we'd need 10,005 objects.
        // That is small enough for TestPdfFactory (a few MBs).
        // Let's generate 10050 objects:
        for (int i = 0; i < 10050; i++)
        {
            if (i < 10049)
            {
                objects.Add($"<< /Title (Item{i}) /Next {i + 6} 0 R >>");
            }
            else
            {
                objects.Add($"<< /Title (Item{i}) >>");
            }
        }

        byte[] pdf = TestPdfFactory.Serialize(objects, "/Root 1 0 R");
        var doc = PdfDocument.Load(pdf);
        var outline = PdfOutline.Read(doc);

        Assert.Equal(10000, outline.Count);
    }



    [Fact]
    public void Read_MutationTest_Fixture_ProducesOnlyValidResultsOrPdfExceptions()
    {
        var path = Path.Combine("Fixtures", "linked-objstm.pdf");
        if (!File.Exists(path)) {
            path = Path.Combine("..", "..", "..", "..", "Fixtures", "linked-objstm.pdf");
        }
        if (!File.Exists(path)) {
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../tests/FileKit.Pdf.Tests/Fixtures/linked-objstm.pdf");
        }

        byte[] originalData = File.ReadAllBytes(path);

        // Mutate random bytes
        var random = new Random(42);
        for (int i = 0; i < 50; i++)
        {
            byte[] mutatedData = (byte[])originalData.Clone();
            int index = random.Next(mutatedData.Length);
            mutatedData[index] = (byte)random.Next(256);

            try
            {
                var doc = PdfDocument.Load(mutatedData);
                var outline = PdfOutline.Read(doc);
                // If it succeeds, it's a valid result for that mutated file
                Assert.NotNull(outline);
            }
            catch (PdfException)
            {
                // Expected, allowed failure
            }
        }
    }

}
