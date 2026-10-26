using Xunit;

namespace ContextBar.Core.Search.Tests;

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
    [InlineData("completelyunrelated", "Convert to WebP")]
    [InlineData("apple banana", "Compress images")]
    [InlineData("cnvrt", "Convert to WebP")] // Too many missing letters for a short word
    public void CalculateScore_ReturnsZeroForNoMatch(string query, string target)
    {
        int score = FuzzyMatcher.CalculateScore(query, target);
        Assert.Equal(0, score);
    }
}
