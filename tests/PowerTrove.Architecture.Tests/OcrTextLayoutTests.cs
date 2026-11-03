using PowerTrove.Abstractions.Ocr;
using PowerTrove.Desktop;

namespace PowerTrove.Architecture.Tests;

public sealed class OcrTextLayoutTests
{
    // Two lines; the second word of the first line sits lower and is shorter than the first.
    private static readonly RecognizedLine[] Lines =
    [
        new("Die Datei", [new("Die", 10, 10, 30, 20), new("Datei", 50, 14, 50, 14)]),
        new("", []),
        new("öffnen jetzt", [new("jetzt", 90, 60, 40, 20), new("öffnen", 10, 60, 70, 20)]),
    ];

    [Fact]
    public void WordsOfALineShareTopAndHeight()
    {
        var layout = new OcrTextLayout(Lines);
        Assert.Equal(4, layout.Count);
        Assert.Equal(layout.Box(0).Y, layout.Box(1).Y);
        Assert.Equal(layout.Box(0).Height, layout.Box(1).Height);
        Assert.True(layout.Box(0).Y < 10 && layout.Box(0).Y + layout.Box(0).Height > 30);
    }

    [Fact]
    public void ARangeReadsInLineOrderWithLineBreaks()
    {
        var layout = new OcrTextLayout(Lines);
        Assert.Equal("Datei" + Environment.NewLine + "öffnen", layout.Text(2, 1));
        Assert.Equal("Die Datei" + Environment.NewLine + "öffnen jetzt", layout.Text(0, 3));
        Assert.Equal(2, layout.SelectionBoxes(0, 3).Count());
    }

    [Fact]
    public void HitTestPicksTheNearestLineThenWord()
    {
        var layout = new OcrTextLayout(Lines);
        Assert.Equal(0, layout.HitTest(0, 0));
        Assert.Equal(1, layout.HitTest(500, 22));
        Assert.Equal(2, layout.HitTest(20, 300));
        Assert.Equal(-1, new OcrTextLayout([]).HitTest(1, 1));
    }

    [Fact]
    public void LinesWithoutWordBoxesHaveNoRange()
    {
        var layout = new OcrTextLayout(Lines);
        Assert.Equal((0, 1), layout.LineRange(0));
        Assert.Null(layout.LineRange(1));
        Assert.Equal((2, 3), layout.LineRange(2));
    }

    [Fact]
    public void JapaneseCharactersJoinWithoutSpaces()
    {
        var layout = new OcrTextLayout([new("日本 語", [new("日本", 0, 0, 20, 10), new("語", 22, 0, 10, 10)])]);
        Assert.Equal("日本語", layout.Text(0, 1));
    }
}
