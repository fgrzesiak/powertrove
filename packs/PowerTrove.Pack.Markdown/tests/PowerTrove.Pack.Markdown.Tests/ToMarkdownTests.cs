using System.Text;
using PowerTrove.Pack.Markdown.Conversion;
using PowerTrove.Pack.Markdown.Markdig;
using PowerTrove.Pack.Markdown.Pdf;
using static PowerTrove.Pack.Markdown.Tests.TestDocuments;

namespace PowerTrove.Pack.Markdown.Tests;

public sealed class ToMarkdownTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FileActionServices _services = FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
    private readonly MarkdigMarkdownEngine _engine = new();
    public void Dispose() => _dir.Dispose();

    private ToMarkdownAction Action(IPdfTextReader? reader = null) => new(_services, _engine, reader ?? new PdfLayoutTextReader());

    private async Task<(string Markdown, ActionReceipt Receipt)> ConvertAsync(string name, byte[] content, params (string, string)[] parameters)
    {
        string path = _dir.Write(name, content);
        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(path), parameters.Length == 0 ? new EmptyParameters() : Snapshots.Parameters(parameters), null, default);
        Assert.True(receipt.IsSuccess, string.Join("; ", receipt.Failures.Select(f => f.ErrorMessage)));
        string markdown = File.ReadAllText(receipt.OutputPaths.Single(p => p.EndsWith(".md", StringComparison.Ordinal)));
        return (markdown, receipt);
    }

    [Fact]
    public async Task WordKeepsHeadingsListsEmphasisLinksTablesFootnotesAndPictures()
    {
        string body = string.Concat(
            P(R("Contents") + R("1"), "Verzeichnis1"),
            P(R("Quarterly report"), "Title"),
            P(R("Overview"), "berschrift1"),
            P(R("Bold", "<w:b/>") + R(" and ") + R("italic", "<w:i/>") + R(" with 5 * 3 ") + "<w:hyperlink r:id=\"rIdLink\">" + R("a link") + "</w:hyperlink>" + R(" and ") + R("code()", "<w:rFonts w:ascii=\"Consolas\"/>")
              + R(".") + "<w:r><w:footnoteReference w:id=\"1\"/></w:r>"),
            P(R("Details"), "berschrift2"),
            P(R("First"), numbering: Num(1)),
            P(R("Nested"), numbering: Num(1, 1)),
            P(R("Second"), numbering: Num(1)),
            P(R("Step one"), numbering: Num(2)),
            P(R("Step two"), numbering: Num(2)),
            P(R("Well said."), "Zitat"),
            "<w:tbl><w:tr><w:tc><w:p>" + R("Name") + "</w:p></w:tc><w:tc><w:p>" + R("Size") + "</w:p></w:tc></w:tr>"
              + "<w:tr><w:tc><w:p>" + R("a|b") + "</w:p></w:tc><w:tc><w:p>" + R("10") + "</w:p></w:tc></w:tr></w:tbl>",
            P(R("var x = 1;", "<w:rFonts w:ascii=\"Courier New\"/>")),
            P(R("return x;", "<w:rFonts w:ascii=\"Courier New\"/>")),
            P(Picture),
            "<w:p><w:r><w:delText>deleted</w:delText></w:r></w:p>");

        var (markdown, receipt) = await ConvertAsync("report.docx", Docx(body));

        Assert.Equal(
            "# Quarterly report\n\n## Overview\n\n**Bold** and *italic* with 5 \\* 3 [a link](https://example.com/a%20b) and `code()`.[^1]\n\n### Details\n\n"
            + "- First\n  - Nested\n- Second\n\n1. Step one\n1. Step two\n\n> Well said.\n\n| Name | Size |\n| --- | --- |\n| a\\|b | 10 |\n\n"
            + "```\nvar x = 1;\nreturn x;\n```\n\n![A red dot](report-images/1.png)\n\n[^1]: A source.\n", markdown);
        Assert.Equal(MarkdownActionTests.Png(), File.ReadAllBytes(_dir.File("report-images/1.png")));
        Assert.Equal(2, receipt.OutputPaths.Count);
        string html = _engine.Render(markdown, default).ToHtml(_ => null, default);
        Assert.Contains("<table>", html);
        Assert.Contains("A source.", html);
    }

    [Fact]
    public async Task WordWithoutHeadingStylesUsesShortBoldParagraphs()
    {
        string body = P(R("Introduction", "<w:b/>")) + P(R("Some text that explains things.")) + P(R("Not a heading.", "<w:b/>"));
        var (markdown, _) = await ConvertAsync("plain.docx", Docx(body, withNumbering: false));
        Assert.Equal("## Introduction\n\nSome text that explains things.\n\n**Not a heading.**\n", markdown);
    }

    [Fact]
    public async Task PicturesCanBeLeftOut()
    {
        var (markdown, receipt) = await ConvertAsync("pic.docx", Docx(P(R("Text")) + P(Picture)), ("Images", "false"));
        Assert.Equal("Text\n\n*A red dot*\n", markdown);
        Assert.Single(receipt.OutputPaths);
        Assert.False(Directory.Exists(_dir.File("pic-images")));
    }

    [Fact]
    public async Task PowerPointBecomesOneSectionPerSlideInReadingOrder()
    {
        string first = Shape("body", 2000, Para("Point one"), Para("Detail", 1), Para("Point two", 0, "b=\"1\""))
            + Shape("title", 100, Para("Welcome"))
            + Shape(null, 5000, "<a:p><a:r><a:t>Footer text box</a:t></a:r></a:p>")
            + "<p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"Pic\" descr=\"Logo\"/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rIdImg\"/></p:blipFill><p:spPr><a:xfrm><a:off x=\"0\" y=\"9000\"/></a:xfrm></p:spPr></p:pic>";
        string second = Shape("body", 100, Para("No title here"))
            + "<p:graphicFrame><p:nvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"3000\"/></p:xfrm><a:graphic><a:graphicData><a:tbl>"
            + "<a:tr><a:tc><a:txBody><a:p><a:r><a:t>Q</a:t></a:r></a:p></a:txBody></a:tc><a:tc><a:txBody><a:p><a:r><a:t>Sales</a:t></a:r></a:p></a:txBody></a:tc></a:tr>"
            + "<a:tr><a:tc><a:txBody><a:p><a:r><a:t>1</a:t></a:r></a:p></a:txBody></a:tc><a:tc><a:txBody><a:p><a:r><a:t>10</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame>";

        var (markdown, _) = await ConvertAsync("deck.pptx", Pptx(first, second));

        Assert.Equal("# deck\n\n## Welcome\n\n- Point one\n  - Detail\n- **Point two**\n\nFooter text box\n\n![Logo](deck-images/1.png)\n\n> **Notes**\n>\n> Say hello first.\n\n"
            + "## Slide 2\n\n- No title here\n\n| Q | Sales |\n| --- | --- |\n| 1 | 10 |\n", markdown);

        var (withoutNotes, _) = await ConvertAsync("deck2.pptx", Pptx(first), ("Notes", "false"), ("Images", "false"));
        Assert.DoesNotContain("Say hello", withoutNotes);
    }

    [Fact]
    public async Task SpreadsheetsBecomeTablesPerSheet()
    {
        var workbook = new FileKit.Xlsx.Workbook([
            new("Sales", [[new FileKit.Xlsx.EmptyCell(), new FileKit.Xlsx.EmptyCell()], [new FileKit.Xlsx.TextCell("Item"), new FileKit.Xlsx.TextCell("Price")], [new FileKit.Xlsx.TextCell("Pen"), new FileKit.Xlsx.NumberCell(1.5)]]),
            new("Empty", []),
            new("Notes", [[new FileKit.Xlsx.TextCell("Text")], [new FileKit.Xlsx.TextCell("a|b")]])]);
        using var memory = new MemoryStream();
        await FileKit.Xlsx.XlsxWriter.WriteAsync(memory, workbook);

        var (markdown, _) = await ConvertAsync("book.xlsx", memory.ToArray());
        Assert.Equal("## Sales\n\n| Item | Price |\n| --- | --- |\n| Pen | 1.5 |\n\n## Notes\n\n| Text |\n| --- |\n| a&#124;b |\n", markdown);
        Assert.False((await Action().MatchAsync(Snapshots.ForPaths(_dir.Write("list.csv", "a,b")), default)).IsAvailable);
    }

    [Fact]
    public async Task NotebooksKeepMarkdownCodeAndOutputs()
    {
        string png = Convert.ToBase64String(MarkdownActionTests.Png());
        string notebook = """
            {"metadata":{"language_info":{"name":"python"}},"nbformat":4,"cells":[
              {"cell_type":"markdown","source":["# Analysis\n","![chart](attachment:dot.png)"],"attachments":{"dot.png":{"image/png":"PNG"}}},
              {"cell_type":"code","source":"print('hi')","outputs":[{"output_type":"stream","name":"stdout","text":["\u001b[31mhi\u001b[0m\n"]},
                {"output_type":"display_data","data":{"image/png":"PNG","text/plain":["<Figure>"]}}]},
              {"cell_type":"code","source":"1/0","outputs":[{"output_type":"error","ename":"ZeroDivisionError","evalue":"division by zero","traceback":[]}]}
            ]}
            """.Replace("PNG", png, StringComparison.Ordinal);

        var (markdown, receipt) = await ConvertAsync("analysis.ipynb", Encoding.UTF8.GetBytes(notebook));
        Assert.Equal("# Analysis\n![chart](analysis-images/1.png)\n\n```python\nprint('hi')\n```\n\n```text\nhi\n```\n\n![](analysis-images/2.png)\n\n```python\n1/0\n```\n\n```text\nZeroDivisionError: division by zero\n```\n", markdown);
        Assert.Equal(2, receipt.OutputPaths.Count);
        Assert.Equal(2, Directory.GetFiles(_dir.File("analysis-images")).Length);

        var (codeOnly, _) = await ConvertAsync("analysis2.ipynb", Encoding.UTF8.GetBytes(notebook), ("Outputs", "false"), ("Images", "false"));
        Assert.DoesNotContain("ZeroDivisionError", codeOnly);
    }

    [Fact]
    public async Task EbooksAreReadInSpineOrderWithTheirPictures()
    {
        byte[] epub = Zip(
            ("mimetype", "application/epub+zip"),
            ("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/book.opf\"/></rootfiles></container>"),
            ("OEBPS/book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\"><manifest><item id=\"nav\" href=\"nav.xhtml\" properties=\"nav\"/><item id=\"c2\" href=\"text/two.xhtml\"/><item id=\"c1\" href=\"text/one.xhtml\"/></manifest>"
                + "<spine><itemref idref=\"nav\"/><itemref idref=\"c1\"/><itemref idref=\"c2\"/></spine></package>"),
            ("OEBPS/nav.xhtml", "<html><body><nav><a href=\"text/one.xhtml\">One</a></nav></body></html>"),
            ("OEBPS/text/one.xhtml", "<html><body><h1>Chapter one</h1><p>See <a href=\"two.xhtml#x\">chapter two</a>.</p><img src=\"../img/dot.png\" alt=\"Dot\"/></body></html>"),
            ("OEBPS/text/two.xhtml", "<html><body><h1>Chapter two</h1><p>The end.</p></body></html>"),
            ("OEBPS/img/dot.png", MarkdownActionTests.Png()));

        var (markdown, _) = await ConvertAsync("novel.epub", epub);
        Assert.Equal("# Chapter one\n\nSee chapter two.\n\n![Dot](novel-images/1.png)\n\n# Chapter two\n\nThe end.\n", markdown);

        byte[] protectedBook = Zip(("META-INF/encryption.xml", "<encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><EncryptedData xmlns=\"http://www.w3.org/2001/04/xmlenc#\"><EncryptionMethod Algorithm=\"http://www.w3.org/2001/04/xmlenc#aes128-cbc\"/></EncryptedData></encryption>"));
        string path = _dir.Write("locked.epub", protectedBook);
        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, default);
        Assert.Contains(receipt.Failures, f => f.ErrorMessage.Contains("copy-protected"));
    }

    [Fact]
    public async Task PdfStructureComesFromFontsPositionsAndRepetition()
    {
        var lines = new List<(int, string, double, double, double, string)>();
        for (int page = 1; page <= 3; page++)
        {
            lines.Add((page, "F1", 9, 72, 760, "ACME Corp - Annual report"));
            lines.Add((page, "F1", 9, 300, 30, page.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        lines.AddRange([
            (1, "F2", 22, 72, 700, "Annual report"),
            (1, "F2", 15, 72, 660, "Introduction"),
            (1, "F1", 11, 72, 630, "This report describes how the year went for the com-"),
            (1, "F1", 11, 72, 616, "pany and what comes next for everyone involved."),
            (1, "F1", 11, 72, 580, " Revenue grew strongly in every region"),
            (1, "F1", 11, 72, 566, " Costs stayed flat"),
            (1, "F1", 11, 72, 520, "The plan for next year continues the work that"),
            (2, "F1", 11, 72, 700, "started this year in all teams."),
            (2, "F2", 15, 72, 660, "Outlook"),
            (2, "F3", 10, 72, 630, "total = sum(sales)"),
            (3, "F1", 11, 72, 700, "Thanks to all readers.")]);

        var (markdown, _) = await ConvertAsync("report.pdf", TestDocuments.Pdf(3, lines));

        Assert.Equal("# Annual report\n\n## Introduction\n\nThis report describes how the year went for the company and what comes next for everyone involved.\n\n"
            + "- Revenue grew strongly in every region\n- Costs stayed flat\n\nThe plan for next year continues the work that started this year in all teams.\n\n"
            + "## Outlook\n\n```\ntotal = sum(sales)\n```\n\nThanks to all readers.\n", markdown);
    }

    [Fact]
    public async Task PdfPicturesAppearWhereTheyAreDrawnAndRepeatedLogosAreDropped()
    {
        var lines = new List<(int, string, double, double, double, string)>();
        for (int page = 1; page <= 3; page++) lines.Add((page, "F1", 11, 72, 700, $"Text on page {page}."));
        lines.Add((1, "F1", 11, 72, 300, "Text below the chart."));
        // A chart between the paragraphs, a tiny ornament and a logo in the corner of every page.
        var images = new List<(int, double, double, double, double, int)> { (1, 72, 400, 200, 150, 0), (1, 300, 600, 10, 10, 0) };
        for (int page = 1; page <= 3; page++) images.Add((page, 500, 740, 60, 30, 1));
        byte[] pdf = TestDocuments.Pdf(3, lines, images: images, pictures: [(255, 0, 0), (0, 0, 255)]);

        var (markdown, receipt) = await ConvertAsync("chart.pdf", pdf);
        Assert.Equal("Text on page 1.\n\n![](chart-images/1.png)\n\nText below the chart.\n\nText on page 2.\n\nText on page 3.\n", markdown);
        Assert.Single(Directory.GetFiles(_dir.File("chart-images")));
        Assert.Equal(2, receipt.OutputPaths.Count);

        var (withoutPictures, plain) = await ConvertAsync("chart2.pdf", pdf, ("Images", "false"));
        Assert.Equal("Text on page 1.\n\nText below the chart.\n\nText on page 2.\n\nText on page 3.\n", withoutPictures);
        Assert.Single(plain.OutputPaths);
    }

    [Fact]
    public async Task ScannedPdfsExplainThatTextRecognitionIsNeeded()
    {
        string path = _dir.Write("scan.pdf", TestDocuments.Pdf(1, []));
        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, default);
        Assert.Contains(receipt.Failures, f => f.ErrorMessage.Contains("text recognition"));
        Assert.Equal([path], _dir.NewFiles());
    }

    [Fact]
    public void PdfTablesWithDrawnBordersBecomeMarkdownTables()
    {
        PdfTextWord Word(string text, double x, double y) => new(text, x, y, x + 6 * text.Length, y + 10, 10);
        var page = new PdfTextPage(1, 612, 792,
        [
            new([new([Word("Before", 72, 700)])]),
            new([new([Word("Name", 80, 650), Word("Qty", 200, 650)]), new([Word("Pen", 80, 630), Word("2", 200, 630)])]),
            new([new([Word("After", 72, 580)])])
        ],
        [
            new(70, 665, 300, 665), new(70, 645, 300, 645), new(70, 625, 300, 625),
            new(70, 625, 70, 665), new(190, 625, 190, 665), new(300, 625, 300, 665)
        ]);
        var markdown = PdfLayoutConverter.Convert(new PdfTextDocument([page]), default).Markdown;
        Assert.Equal("Before\n\n| Name | Qty |\n| --- | --- |\n| Pen | 2 |\n\nAfter\n", markdown);
    }

    [Fact]
    public async Task UnsupportedOrDamagedFilesFailWithoutOutput()
    {
        string path = _dir.Write("broken.docx", "not a zip");
        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, default);
        Assert.False(receipt.IsSuccess);
        Assert.Contains(receipt.Failures, f => f.ErrorMessage.Contains("damaged"));
        Assert.Equal([path], _dir.NewFiles());
        Assert.False((await Action().MatchAsync(Snapshots.ForPaths(_dir.Write("a.doc", "x")), default)).IsAvailable);
        var noReader = await new ToMarkdownAction(_services, _engine, null).ExecuteAsync(Snapshots.ForPaths(_dir.Write("a.pdf", "x")), new EmptyParameters(), null, default);
        Assert.False(noReader.IsSuccess);
    }

    [Fact]
    public async Task HtmlStillConvertsAndMarkdownTextIsEscaped()
    {
        var (markdown, _) = await ConvertAsync("page.html", Encoding.UTF8.GetBytes("<h1>Hi</h1><p>1. not a list * star</p>"));
        Assert.Equal("# Hi\n\n1\\. not a list \\* star\n", markdown);
    }
}
