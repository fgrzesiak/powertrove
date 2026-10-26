using PowerTrove.Abstractions.Ocr;

namespace PowerTrove.Abstractions.Tests;

public class OcrCoordinateMappingTests
{
    [Fact]
    public void Coordinates_MapBackToOriginalPixels()
    {
        var word = new RecognizedWord("synthetic", 10, 20, 25, 30);
        var mapped = OcrCoordinateMapping.ToOriginal(word, 1000, 750, 400, 300);
        Assert.Equal(new RecognizedWord("synthetic", 25, 50, 62.5, 75), mapped);
    }
    [Fact]
    public void Coordinates_ClampNegativeAndOversizedBoxes()
    {
        var mapped = OcrCoordinateMapping.ToOriginal(new RecognizedWord("synthetic", -5, 45, 10, 20), 100, 100, 50, 50);
        Assert.Equal((0d, 90d, 10d, 10d), (mapped.X, mapped.Y, mapped.Width, mapped.Height));
        Assert.Throws<ArgumentOutOfRangeException>(() => OcrCoordinateMapping.ToOriginal(new("x", double.NaN, 0, 1, 1), 100, 100, 50, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => OcrCoordinateMapping.ToOriginal(new("x", 0, 0, 1, 1), 100, 100, 0, 50));
    }
}
