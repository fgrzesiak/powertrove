using PowerTrove.Pack.Markdown.Conversion;
using PowerTrove.Pack.Markdown.Markdig;

namespace PowerTrove.Pack.Markdown.Tests;

/// <summary>Bookmarks, tables of contents and links in PDFs, converted from hand-built positioned pages.</summary>
public sealed class PdfStructureTests
{
    private static PdfTextWord Word(string text, double x, double y, double size = 10, bool bold = false) =>
        new(text, x, y, x + size * 0.5 * text.Length, y + size, size, bold);

    /// <summary>A line of words starting at x, one block per line unless grouped by the caller.</summary>
    private static PdfTextLine Line(string text, double x, double y, double size = 10, bool bold = false)
    {
        var words = new List<PdfTextWord>();
        foreach (string part in text.Split(' '))
        {
            words.Add(Word(part, x, y, size, bold));
            x += size * 0.5 * (part.Length + 1);
        }
        return new PdfTextLine(words);
    }

    private static PdfTextPage Page(int number, IReadOnlyList<PdfTextBlock> blocks, IReadOnlyList<PdfTextLink>? links = null) =>
        new(number, 612, 792, blocks, [], null, links);

    private static string Convert(PdfTextDocument document) => PdfLayoutConverter.Convert(document, default).Markdown;

    private static PdfTextPage Body(int number, string heading, string text, bool bold = true) => Page(number,
    [
        new([Line(heading, 72, 650, 10, bold)]),
        new([Line(text, 72, 600)])
    ]);

    [Fact]
    public void BookmarksDecideHeadingsAndLevels()
    {
        var page = Page(1,
        [
            new([Line("Overview", 72, 700)]),
            new([Line("The system has two parts.", 72, 680)]),
            new([Line("Scope", 72, 650)]),
            new([Line("Only the first part is covered.", 72, 630)]),
            new([Line("LABEL", 300, 560, 18)]),
            new([Line("Body text continues here.", 72, 520)])
        ]);
        var markdown = Convert(new PdfTextDocument([page], Outline: [new("Overview", 1, 1, 712), new("Scope", 2, 1, 662)]));

        Assert.Equal("# Overview\n\nThe system has two parts.\n\n## Scope\n\nOnly the first part is covered.\n\nLABEL\n\nBody text continues here.\n", markdown);
    }

    [Fact]
    public void DotLeaderContentsBecomeANestedListLinkedToTheHeadings()
    {
        var contents = Page(1,
        [
            new([Line("Contents", 72, 720, 14)]),
            new([Line("1 Intro ........ 2", 72, 690), Line("1.1 Scope ........ 2", 72, 675), Line("2 Usage ........ 3", 72, 660)])
        ]);
        var document = new PdfTextDocument([contents, Body(2, "1 Intro", "Some words."), Body(3, "2 Usage", "More words.")]);
        string markdown = Convert(document with
        {
            Pages = [contents, Page(2, [new([Line("1 Intro", 72, 650, 10, true)]), new([Line("1.1 Scope", 72, 620, 10, true)]), new([Line("Text.", 72, 600)])]), document.Pages[2]]
        });

        Assert.Contains("- [1 Intro](#1-intro)\n  - [1.1 Scope](#11-scope)\n- [2 Usage](#2-usage)", markdown);
        Assert.Contains("## 1.1 Scope", markdown);
        string html = new MarkdigMarkdownEngine().Render(markdown, default).ToHtml(_ => null, default);
        Assert.Contains("id=\"11-scope\"", html);
        Assert.Contains("href=\"#11-scope\"", html);
    }

    [Fact]
    public void RepeatedTitlesInTheContentsLinkToTheirOwnHeading()
    {
        var contents = Page(1,
        [
            new([Line("Contents", 72, 720, 14)]),
            new([Line("1 Alpha ...... 2", 72, 690), Line("References ...... 2", 90, 675), Line("2 Beta ...... 3", 72, 660), Line("References ...... 3", 90, 645)])
        ]);
        PdfTextPage Chapter(int number, string title) => Page(number,
        [
            new([Line(title, 72, 700, 10, true)]),
            new([Line("Text of the chapter.", 72, 680)]),
            new([Line("References", 72, 650, 10, true)]),
            new([Line("A source.", 72, 630)])
        ]);
        string markdown = Convert(new PdfTextDocument([contents, Chapter(2, "1 Alpha"), Chapter(3, "2 Beta")]));

        Assert.Contains("- [1 Alpha](#1-alpha)\n  - [References](#references)\n- [2 Beta](#2-beta)\n  - [References](#references-1)", markdown);
    }

    [Fact]
    public void RightAlignedAscendingPageNumbersAreContentsWithoutLeaders()
    {
        PdfTextLine Entry(string title, string number, double y) =>
            new([.. Line(title, 72, y).Words, Word(number, 500 - 5 * number.Length, y)]);
        var contents = Page(1, [new([Entry("Setup", "2", 700), Entry("Knobs", "2", 680), Entry("Pads", "3", 660), Entry("Screen", "3", 640)])]);
        string markdown = Convert(new PdfTextDocument([contents, Body(2, "Setup", "Plug it in."), Body(3, "Screen", "Shows values.")]));

        Assert.StartsWith("- [Setup](#setup)\n- Knobs\n- Pads\n- [Screen](#screen)\n", markdown);
    }

    [Fact]
    public void LinkAreasBecomeLinksToAddressesAndHeadings()
    {
        var first = Page(1,
        [
            new([Line("See the website or the details for more.", 72, 700)])
        ],
        [
            new(72 + 5 * 8, 698, 72 + 5 * 15, 712, "https://example.com/a b", null, null),
            new(72 + 5 * 23, 698, 72 + 5 * 30, 712, null, 2, 670)
        ]);
        string markdown = Convert(new PdfTextDocument([first, Body(2, "Details", "Everything else.")], Outline: [new("Details", 1, 2, null), new("Missing", 1, 2, null)]));

        Assert.StartsWith("See the [website](<https://example.com/a b>) or the [details](#details) for more.\n", markdown);
    }

    [Fact]
    public void LinksToOtherSchemesAreNotKeptByTheReader()
    {
        // The FileKit reader keeps only http, https and mailto; a page without links converts as plain text.
        string markdown = Convert(new PdfTextDocument([Page(1, [new([Line("Plain words only.", 72, 700)])], [])]));

        Assert.Equal("Plain words only.\n", markdown);
    }
}
