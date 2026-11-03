using PowerTrove.Pack.Markdown.Conversion;
using PowerTrove.Pack.Markdown.Markdig;

namespace PowerTrove.Pack.Markdown.Tests;

/// <summary>Footnotes and superscripts in PDFs, converted from hand-built positioned pages.</summary>
public sealed class PdfFootnoteTests
{
    private static PdfTextWord Word(string text, double x, double y, double size = 10, int raisedFrom = -1) =>
        new(text, x, y, x + size * 0.5 * text.Length, y + size, size, RaisedFrom: raisedFrom);

    private static PdfTextLine Line(string text, double x, double y, double size = 10)
    {
        var words = new List<PdfTextWord>();
        foreach (string part in text.Split(' '))
        {
            words.Add(Word(part, x, y, size));
            x += size * 0.5 * (part.Length + 1);
        }
        return new PdfTextLine(words);
    }

    private static PdfTextPage Page(int number, params PdfTextBlock[] blocks) => new(number, 612, 792, blocks, [], null);

    private static string Convert(params PdfTextPage[] pages) => PdfLayoutConverter.Convert(new PdfTextDocument(pages), default).Markdown;

    /// <summary>Body text at 10 pt with a word carrying a raised mark, and a note at 8 pt at the foot of the page.</summary>
    private static PdfTextPage NotePage(int number, string marked, int raisedFrom, params PdfTextLine[] notes) => Page(number,
        new PdfTextBlock([Line("Switches deliver data between devices on one network segment.", 72, 700)]),
        new PdfTextBlock([new PdfTextLine([Word("They", 72, 680), Word("forward", 97, 680), Word(marked, 135, 680, raisedFrom: raisedFrom), Word("quickly.", 175, 680)])]),
        new PdfTextBlock(notes));

    [Fact]
    public void ARaisedMarkWithANoteAtTheFootOfThePageBecomesAFootnote()
    {
        string markdown = Convert(NotePage(1, "frames1", 6,
            Line("1A frame is a unit of data with its payload and", 72, 90, 8), Line("synchronization bits.", 72, 80, 8)));

        Assert.Contains("They forward frames[^1] quickly.", markdown);
        Assert.EndsWith("[^1]: A frame is a unit of data with its payload and synchronization bits.\n", markdown);
        Assert.DoesNotContain("1A frame", markdown);
        string html = new MarkdigMarkdownEngine().Render(markdown, default).ToHtml(_ => null, default);
        Assert.Contains("footnote-ref", html);
        Assert.Contains("A frame is a unit of data", html);
    }

    [Fact]
    public void AMarkSetApartFromItsWordFollowsTheWordDirectly()
    {
        var page = Page(1,
            new PdfTextBlock([Line("Switches deliver data between devices on one network segment.", 72, 700)]),
            new PdfTextBlock([new PdfTextLine([Word("Use", 72, 680), Word("RPL", 92, 680), Word("2", 110, 684, 6), Word("here.", 116, 680)])]),
            new PdfTextBlock([Line("2 Pronounced as ripple.", 72, 80, 8)]));

        string markdown = Convert(page);

        Assert.Contains("Use RPL[^1] here.", markdown);
        Assert.Contains("[^1]: Pronounced as ripple.", markdown);
    }

    [Fact]
    public void NotesAreNumberedThroughTheDocumentWhenChaptersStartAgain()
    {
        string markdown = Convert(
            NotePage(1, "frames1", 6, Line("1First note.", 72, 80, 8)),
            NotePage(2, "frames1", 6, Line("1Second note.", 72, 80, 8)));

        Assert.Contains("[^1]: First note.", markdown);
        Assert.Contains("[^2]: Second note.", markdown);
        Assert.Contains("frames[^2]", markdown);
    }

    [Fact]
    public void ARaisedTailWithoutANoteIsASuperscript()
    {
        // An exponent after a number, and an index without a note on the page.
        string markdown = Convert(Page(1,
            new PdfTextBlock([Line("Switches deliver data between devices on one network segment.", 72, 700)]),
            new PdfTextBlock([new PdfTextLine([Word("IPv6", 72, 680), Word("allows", 97, 680), Word("2128", 130, 680, raisedFrom: 1), Word("addresses,", 155, 680),
                Word("says", 205, 680), Word("Tonella2", 230, 680, raisedFrom: 7)])])));

        Assert.Contains("IPv6 allows 2<sup>128</sup> addresses, says Tonella<sup>2</sup>", markdown);
        Assert.DoesNotContain("[^", markdown);
    }

    [Fact]
    public void ANumberWithoutTextIsNoNote()
    {
        // A small "18" low on the page (an axis label) does not turn the exponent of 10^18 into a footnote.
        string markdown = Convert(NotePage(1, "1018", 2, Line("18", 72, 80, 8)));

        Assert.Contains("10<sup>18</sup>", markdown);
        Assert.DoesNotContain("[^", markdown);
    }
}
