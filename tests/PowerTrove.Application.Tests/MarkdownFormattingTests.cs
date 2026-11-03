using PowerTrove.Application.Documents;

namespace PowerTrove.Application.Tests;

public sealed class MarkdownFormattingTests
{
    private static string Apply(string text, TextEdit edit) => text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];
    private static string Selected(string text, TextEdit edit) => Apply(text, edit).Substring(edit.SelectionStart, edit.SelectionLength);

    [Fact]
    public void BoldWrapsTheSelectionAndUnwrapsItAgain()
    {
        string text = "Hello world";
        var bold = MarkdownFormatting.ToggleWrap(text, 6, 5, "**", "bold text");
        string once = Apply(text, bold);
        Assert.Equal("Hello **world**", once);
        Assert.Equal("world", Selected(text, bold));
        var again = MarkdownFormatting.ToggleWrap(once, bold.SelectionStart, bold.SelectionLength, "**", "bold text");
        Assert.Equal(text, Apply(once, again));
    }

    [Fact]
    public void WrappingKeepsSpacesOutsideAndInsertsAPlaceholderWithoutSelection()
    {
        Assert.Equal("a *word* b", Apply("a word b", MarkdownFormatting.ToggleWrap("a word b", 1, 6, "*", "x")));
        var empty = MarkdownFormatting.ToggleWrap("ab", 1, 0, "~~", "text");
        Assert.Equal("a~~text~~b", Apply("ab", empty));
        Assert.Equal("text", Selected("ab", empty));
    }

    [Fact]
    public void ItalicDoesNotStripTheMarkersOfBoldText()
    {
        string text = "**bold**";
        Assert.Equal("***bold***", Apply(text, MarkdownFormatting.ToggleWrap(text, 2, 4, "*", "x")));
    }

    [Theory]
    [InlineData(MarkdownLineStyle.Heading1, "# Title")]
    [InlineData(MarkdownLineStyle.Heading2, "## Title")]
    [InlineData(MarkdownLineStyle.Quote, "> Title")]
    [InlineData(MarkdownLineStyle.Paragraph, "Title")]
    public void LineStylesReplaceTheCurrentPrefix(MarkdownLineStyle style, string expected)
    {
        string text = "### Title";
        Assert.Equal(expected, Apply(text, MarkdownFormatting.SetLineStyle(text, 5, 0, style)));
    }

    [Fact]
    public void ListsNumberEverySelectedLineAndToggleOff()
    {
        // WinUI text boxes report line breaks as "\r".
        string text = "one\r- two\rthree\r\rafter";
        var numbered = MarkdownFormatting.SetLineStyle(text, 0, 15, MarkdownLineStyle.Numbered);
        string list = Apply(text, numbered);
        Assert.Equal("1. one\r2. two\r3. three\r\rafter", list);
        var off = MarkdownFormatting.SetLineStyle(list, numbered.SelectionStart, numbered.SelectionLength, MarkdownLineStyle.Numbered);
        Assert.Equal("one\rtwo\rthree\r\rafter", Apply(list, off));
        Assert.Equal("- [ ] task", Apply("task", MarkdownFormatting.SetLineStyle("task", 2, 0, MarkdownLineStyle.Task)));
        Assert.Equal("- a\n\n- b", Apply("a\n\nb", MarkdownFormatting.SetLineStyle("a\n\nb", 0, 4, MarkdownLineStyle.Bullet)));
    }

    [Fact]
    public void LinksAndImagesSelectWhatTheUserTypesNext()
    {
        var link = MarkdownFormatting.InsertLink("see docs", 4, 4, "link text");
        Assert.Equal("see [docs](https://)", Apply("see docs", link));
        Assert.Equal("https://", Selected("see docs", link));
        var empty = MarkdownFormatting.InsertLink("", 0, 0, "link text");
        Assert.Equal("link text", Selected("", empty));
        var image = MarkdownFormatting.InsertImage("", 0, 0, "description", "img/my photo.png");
        Assert.Equal("![description](<img/my photo.png>)", Apply("", image));
        Assert.Equal("description", Selected("", image));
    }

    [Fact]
    public void CodeIsInlineWithinALineAndFencedForWholeLines()
    {
        Assert.Equal("run `dotnet` now", Apply("run dotnet now", MarkdownFormatting.ToggleCode("run dotnet now", 4, 6, "\n", "code")));
        string text = "intro\nvar a = 1;\nvar b = 2;\nend";
        var block = MarkdownFormatting.ToggleCode(text, 6, 21, "\n", "code");
        Assert.Equal("intro\n\n```\nvar a = 1;\nvar b = 2;\n```\n\nend", Apply(text, block));
        Assert.Equal("var a = 1;\nvar b = 2;", Selected(text, block));
    }

    [Fact]
    public void TablesAndRulesStandOnTheirOwnLines()
    {
        var table = MarkdownFormatting.InsertTable("Text", 4, 0, "\n", "Column", "Text");
        Assert.Equal("Text\n\n| Column 1 | Column 2 |\n| --- | --- |\n| Text | Text |\n", Apply("Text", table));
        Assert.Equal("Column 1", Selected("Text", table));
        Assert.Equal("a\n\n---\n\nb", Apply("a\nb", MarkdownFormatting.InsertBlock("a\nb", 2, 0, "---", "\n")));
    }
}
