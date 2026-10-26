using ContextBar.Pack.Pdf.Windows;

namespace ContextBar.Pack.Pdf.Windows.Tests;

public sealed class WindowsPdfRendererTests
{
    [Theory]
    [InlineData(100, 200, 150, 100, 200)]
    [InlineData(600, 800, 150, 150, 200)]
    [InlineData(1, 1_000_000_000, 150, 1, 16_384)]
    [InlineData(1_000_000_000, 1, int.MaxValue, 16_384, 1)]
    public void GrayRenderSize_PreservesNormalSizesAndCapsExtremeSides(
        double width, double height, int maxWidth, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), WindowsPdfRenderer.GetGrayRenderSize(width, height, maxWidth));
    }

    [Theory]
    [InlineData(16_384, 16_384)]
    [InlineData(double.MaxValue, double.MaxValue)]
    [InlineData(double.Epsilon, double.MaxValue)]
    [InlineData(double.MaxValue, double.Epsilon)]
    public void GrayRenderSize_BoundsTotalPixelsAndBothSides(double width, double height)
    {
        var size = WindowsPdfRenderer.GetGrayRenderSize(width, height, int.MaxValue);

        Assert.InRange(size.Width, 1, 16_384);
        Assert.InRange(size.Height, 1, 16_384);
        Assert.InRange((long)size.Width * size.Height, 1L, 80_000_000L);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void GrayRenderSize_RejectsInvalidDimensions(double dimension)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPdfRenderer.GetGrayRenderSize(dimension, 100, 150));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPdfRenderer.GetGrayRenderSize(100, dimension, 150));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RenderGrayAsync_RejectsInvalidWidthBeforeOpeningFile(int maxWidth)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new WindowsPdfRenderer().RenderGrayAsync(string.Empty, 0, maxWidth, CancellationToken.None));
    }
}
