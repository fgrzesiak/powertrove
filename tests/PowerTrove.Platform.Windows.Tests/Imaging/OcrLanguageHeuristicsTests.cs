using PowerTrove.Abstractions.Ocr;
using PowerTrove.Platform.Windows.Imaging;

namespace PowerTrove.Platform.Windows.Tests.Imaging;

public sealed class OcrLanguageHeuristicsTests
{
    private static RecognizedLine Line(string text) => new(text, Array.Empty<RecognizedWord>());

    [Fact]
    public void JapaneseScriptRanksAboveLatinEngines()
    {
        var lines = new[] { Line("日本語の文章を読み取ります") };
        Assert.True(OcrLanguageHeuristics.Score("ja-JP", lines) >
                    OcrLanguageHeuristics.Score("en-US", lines));
    }

    [Fact]
    public void CommonWordsHelpLatinLanguageSelection()
    {
        var lines = new[] { Line("Die Datei speichern und öffnen"), Line("Nicht für das Programm") };
        Assert.True(OcrLanguageHeuristics.Score("de-DE", lines) >
                    OcrLanguageHeuristics.Score("en-US", lines));
    }

    [Fact]
    public void EmptyRecognitionsAreNotPreferred()
    {
        Assert.Equal(double.NegativeInfinity,
            OcrLanguageHeuristics.Score("de-DE", [Line("")]));
    }

    [Fact]
    public void CandidatePoolIsBoundedAndPrioritizesInstalledProfileLanguages()
    {
        var languages = new[] { "en-US", "de-DE", "ja-JP", "fr-FR", "pl-PL" };
        Assert.Equal(new[] { "pl-PL", "ja-JP", "en-US" },
            OcrLanguageHeuristics.CandidateLanguages(languages, ["pl-PL", "ja"], 3));
        Assert.Equal(Array.Empty<string>(),
            OcrLanguageHeuristics.CandidateLanguages([], ["de"]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OcrLanguageHeuristics.CandidateLanguages(languages, [], 0));
    }
}
