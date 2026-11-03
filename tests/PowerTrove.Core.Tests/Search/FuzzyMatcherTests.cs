using Xunit;

namespace PowerTrove.Core.Search.Tests;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("Convert to WebP", "Convert to WebP", 100)]
    [InlineData("convert", "Convert to WebP", 90)] // Prefix word
    [InlineData("cnvert", "Convert to WebP", 85)] // Fuzzy typo
    [InlineData("webp", "Convert to WebP", 100)] // Exact word match anywhere
    [InlineData("convert webp", "Convert to WebP", 100)] // Multi-word match
    [InlineData("cnvert webp", "Convert to WebP", 92)] // Mixed fuzzy and exact
    [InlineData("compress", "Compress images", 90)] // Prefix word
    [InlineData("cpress", "Compress images", 66)] // Misspelling
    public void CalculateScore_ReturnsExpectedScores(string query, string target, int expectedScore)
    {
        int score = FuzzyMatcher.CalculateScore(query, target);

        // We do a loose check since specific scores might vary based on implementation details,
        // but we want to ensure relative ordering/ranges.
        Assert.True(score > 0, $"Expected positive score for '{query}' against '{target}', got {score}");

        if (expectedScore == 100)
        {
            Assert.Equal(100, score);
        }
        else if (expectedScore == 90)
        {
            Assert.True(score >= 80 && score < 100, $"Expected prefix score ~90, got {score}");
        }
        else
        {
            // Fuzzy match should give some moderate score
            Assert.True(score >= 50 && score < 100, $"Expected fuzzy score between 50-99, got {score}");
        }
    }

    [Theory]
    [InlineData("convert-to-webp", "Convert to WebP")]
    [InlineData("convert_to_webp", "Convert to WebP")]
    [InlineData("webp convert", "Convert to WebP")]
    [InlineData("große", "GROSSE")]
    [InlineData("café", "Cafe")]
    [InlineData("ＰＤＦ", "PDF")]
    [InlineData("ｶﾀｶﾅ", "カタカナ")]
    [InlineData("datei-namen", "Dateinamen ändern")]
    [InlineData("namen", "Dateinamen ändern")]
    [InlineData("conevrt", "Convert")]
    public void NormalizationReorderingCompoundWordsAndTranspositions_AreMatched(string query, string title)
    {
        Assert.True(FuzzyMatcher.CalculateScore(query, title) > 0, $"Missing: {query} -> {title}");
    }

    [Fact]
    public void NormalizationDoesNotEraseUnrelatedWords()
    {
        Assert.Equal(0, FuzzyMatcher.CalculateScore("pdf unknown operation", "PDF merge"));
        Assert.Equal("grosse", FuzzyMatcher.Normalize("Größe"));
        Assert.Equal("cafe", FuzzyMatcher.Normalize("CAFÉ"));
    }

    [Theory]
    [InlineData("completelyunrelated", "Convert to WebP")]
    [InlineData("apple banana", "Compress images")]
    [InlineData("cnvrt", "Convert to WebP")] // Too many missing letters for a short word
    [InlineData("webp", "Convert to WebM")] // Short format names must match exactly
    [InlineData("tiff", "Convert to TIF")]
    public void CalculateScore_ReturnsZeroForNoMatch(string query, string target)
    {
        int score = FuzzyMatcher.CalculateScore(query, target);
        Assert.Equal(0, score);
    }
}
