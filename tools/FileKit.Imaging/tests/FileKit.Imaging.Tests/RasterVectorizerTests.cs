using System.Xml.Linq;
using FileKit.Imaging.Raster;
using FileKit.Imaging.Vector;

namespace FileKit.Imaging.Tests;

public sealed class RasterVectorizerTests
{
    [Fact]
    public void FlatRegionsBecomeCompactClosedPathsAndTransparentHolesSurvive()
    {
        var image = RasterOperations.Canvas(8, 8, new(0, 0, 255));
        for (int y = 2; y < 6; y++) for (int x = 2; x < 6; x++) Array.Clear(image.Bgra, (y * 8 + x) * 4, 4);
        var result = RasterVectorizer.Trace(image, new()); var svg = XDocument.Parse(result.Svg); XNamespace ns = "http://www.w3.org/2000/svg";
        var path = Assert.Single(svg.Root!.Elements(ns + "path")); Assert.Equal("evenodd", path.Attribute("fill-rule")!.Value);
        Assert.Equal("#FF0000", path.Attribute("fill")!.Value); Assert.Equal(2, result.Contours); Assert.Equal(8, result.Segments);
        Assert.Equal(2, path.Attribute("d")!.Value.Count(c => c == 'Z')); Assert.Equal(2, path.Attribute("d")!.Value.Count(c => c == 'M'));
        Assert.DoesNotContain("<image", result.Svg); Assert.DoesNotContain("base64", result.Svg); Assert.DoesNotContain("href", result.Svg);
    }
    [Fact]
    public void DiagonallyTouchingPixelsHaveSeparateContoursAndMonochromeUsesBrightnessThreshold()
    {
        var image = RasterOperations.Canvas(2, 2, new(0, 0, 0, 0));
        image.Bgra[3] = 255; image.Bgra[15] = 255;
        var vector = RasterVectorizer.Trace(image, new(true, "high")); Assert.Equal(2, vector.Contours); Assert.Contains("#000000", vector.Svg);
        var gray = RasterOperations.Canvas(2, 2, new(100, 100, 100));
        Assert.Contains("#000000", RasterVectorizer.Trace(gray, new(true, Threshold: 128)).Svg);
        Assert.Contains("#FFFFFF", RasterVectorizer.Trace(gray, new(true, Threshold: 50)).Svg);
    }
    [Fact]
    public void DetailLimitsWorkingResolutionWhileOriginalDisplayDimensionsRemain()
    {
        var image = RasterOperations.Canvas(1200, 600, new(50, 100, 200));
        var low = XDocument.Parse(RasterVectorizer.Trace(image, new(Detail: "low")).Svg).Root!;
        var high = XDocument.Parse(RasterVectorizer.Trace(image, new(Detail: "high")).Svg).Root!;
        Assert.Equal("1200", low.Attribute("width")!.Value); Assert.Equal("600", low.Attribute("height")!.Value);
        Assert.Equal("0 0 256 128", low.Attribute("viewBox")!.Value); Assert.Equal("0 0 1024 512", high.Attribute("viewBox")!.Value);
    }
    [Fact]
    public void CancellationInvisibleInputAndExcessiveContourComplexityFailExplicitly()
    {
        var invisible = RasterOperations.Canvas(4, 4, new(0, 0, 0, 0));
        Assert.Throws<RasterException>(() => RasterVectorizer.Trace(invisible, new()));
        Assert.ThrowsAny<OperationCanceledException>(() => RasterVectorizer.Trace(invisible, new(), new(true)));
        Assert.Throws<RasterException>(() => RasterVectorizer.Trace(invisible, new(Detail: "unknown")));
        var checker = RasterOperations.Canvas(512, 512, new(0, 0, 0));
        for (int y = 0; y < 512; y++) for (int x = 0; x < 512; x++) if ((x + y) % 2 == 0) checker.Bgra[(y * 512 + x) * 4] = checker.Bgra[(y * 512 + x) * 4 + 1] = checker.Bgra[(y * 512 + x) * 4 + 2] = 255;
        Assert.Throws<RasterException>(() => RasterVectorizer.Trace(checker, new(true, "high")));
    }
}
