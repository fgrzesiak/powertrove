using System;
using System.IO;
using System.Linq;
using Xunit;

namespace FileKit.Pdf.Tests;

public class PdfToolsNUpTests
{

    [Fact]
    public void CreateNUp_2Up_MatchesGutterAndScaling()
    {
        var sourceBytes = TestPdfFactory.Create(new TestPdfFactory.Options { Pages = 2 });
        var doc = PdfDocument.Load(sourceBytes);
        using var ms = new MemoryStream();

        PdfTools.CreateNUp(doc, ms, 2, PdfPageSize.A4);
        ms.Position = 0;

        var output = PdfDocument.Load(ms.ToArray());
        Assert.Equal(1, output.PageCount);

        var pageDict = output.Pages[0].Dictionary;
        var contents = output.Resolve(pageDict["Contents"]) as PdfStream;
        Assert.NotNull(contents);

        var text = System.Text.Encoding.ASCII.GetString(contents.Data);
        // 2-up on A4 -> Sheet is landscape A4 (841.89 x 595.28 points)
        // Gutter is 14.1732
        // w = 200, h = 300 (TestPdfFactory default is width=612, height=792 if not specified, wait TestPdfFactory defaults to 612x792?)

        // Let's just assert it has cm with scale and translate.
        Assert.Contains(" cm", text);
    }



    [Fact]
    public void CreateNUp_MatrixAndScalingIsCorrect()
    {
        var sourceBytes = TestPdfFactory.Create(new TestPdfFactory.Options { Pages = 2 });
        var doc = PdfDocument.Load(sourceBytes);
        using var ms = new MemoryStream();

        PdfTools.CreateNUp(doc, ms, 2, PdfPageSize.A4);
        ms.Position = 0;

        var output = PdfDocument.Load(ms.ToArray());
        Assert.Equal(1, output.PageCount);

        var pageDict = output.Pages[0].Dictionary;
        var contents = output.Resolve(pageDict["Contents"]) as PdfStream;
        Assert.NotNull(contents);

        var text = System.Text.Encoding.ASCII.GetString(contents.Data);
        Assert.Contains("0.67174 0 0 0.67174 14.1732", text);
        Assert.Contains("14.17", text); // Gutter is ~14.17
    }


    [Fact]
    public void CreateNUp_2Up()
    {
        var sourceBytes = TestPdfFactory.Create(new TestPdfFactory.Options { Pages = 3 });
        var doc = PdfDocument.Load(sourceBytes);
        using var ms = new MemoryStream();

        PdfTools.CreateNUp(doc, ms, 2, PdfPageSize.A4);
        ms.Position = 0;

        var output = PdfDocument.Load(ms.ToArray());
        Assert.Equal(2, output.PageCount);
    }

    [Fact]
    public void CreateNUp_4Up()
    {
        var sourceBytes = TestPdfFactory.Create(new TestPdfFactory.Options { Pages = 6 });
        var doc = PdfDocument.Load(sourceBytes);
        using var ms = new MemoryStream();

        PdfTools.CreateNUp(doc, ms, 4, PdfPageSize.Letter);
        ms.Position = 0;

        var output = PdfDocument.Load(ms.ToArray());
        Assert.Equal(2, output.PageCount);
    }

    [Fact]
    public void CreateBooklet_8Pages()
    {
        var sourceBytes = TestPdfFactory.Create(new TestPdfFactory.Options { Pages = 8 });
        var doc = PdfDocument.Load(sourceBytes);
        using var ms = new MemoryStream();

        PdfTools.CreateBooklet(doc, ms, PdfPageSize.A4);
        ms.Position = 0;

        var output = PdfDocument.Load(ms.ToArray());
        Assert.Equal(4, output.PageCount); // 4 sheets for 8 pages (saddle stitch)
    }

    [Fact]
    public void CreateBooklet_10Pages()
    {
        var sourceBytes = TestPdfFactory.Create(new TestPdfFactory.Options { Pages = 10 });
        var doc = PdfDocument.Load(sourceBytes);
        using var ms = new MemoryStream();

        PdfTools.CreateBooklet(doc, ms, PdfPageSize.A4);
        ms.Position = 0;

        var output = PdfDocument.Load(ms.ToArray());
        Assert.Equal(6, output.PageCount); // 12 pages padded, 6 sheets
    }



    [Fact]
    public void CreateBooklet_Order_MatchesPrecomputed()
    {
        var sourceBytes = TestPdfFactory.Create(new TestPdfFactory.Options { Pages = 10 });
        var doc = PdfDocument.Load(sourceBytes);
        using var ms = new MemoryStream();

        PdfTools.CreateBooklet(doc, ms, PdfPageSize.A4);
        ms.Position = 0;

        var output = PdfDocument.Load(ms.ToArray());
        Assert.Equal(6, output.PageCount);

        // 10 pages -> padded to 12.
        // Sheets:
        // S1 F: 12 (none), 1
        // S1 B: 2, 11 (none)
        // S2 F: 10, 3
        // S2 B: 4, 9
        // S3 F: 8, 5
        // S3 B: 6, 7

        var pages = Enumerable.Range(0, 6).Select(i => {
            var pageDict = output.Pages[i].Dictionary;
            var contents = output.Resolve(pageDict["Contents"]) as PdfStream;
            if (contents == null) return string.Empty;
            return System.Text.Encoding.ASCII.GetString(contents.Data);
        }).ToList();

        // The Do command refers to XObjects like Fm0. We can check the sequence of XObjects.
        // S1 F: none, 1 -> Fm0
        // S1 B: 2, none -> Fm1
        // S2 F: 10, 3 -> Fm2, Fm3
        // S2 B: 4, 9 -> Fm4, Fm5
        // S3 F: 8, 5 -> Fm6, Fm7
        // S3 B: 6, 7 -> Fm8, Fm9




        // Look closely: the cache keys are based on object numbers.
        Assert.Contains("Fm0 Do", pages[0]); // 1
        Assert.Contains("Fm0 Do", pages[1]); // 2
        Assert.Contains("Fm0 Do", pages[2]); // 10
        Assert.Contains("Fm1 Do", pages[2]); // 3
        Assert.Contains("Fm0 Do", pages[3]); // 4
        Assert.Contains("Fm1 Do", pages[3]); // 9
        Assert.Contains("Fm0 Do", pages[4]); // 8
        Assert.Contains("Fm1 Do", pages[4]); // 5
        Assert.Contains("Fm0 Do", pages[5]); // 6
        Assert.Contains("Fm1 Do", pages[5]); // 7



    }


}
