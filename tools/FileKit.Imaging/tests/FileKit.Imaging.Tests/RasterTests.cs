using FileKit.Imaging.Raster;

namespace FileKit.Imaging.Tests;

public class RasterTests
{
    private static RasterImage Labeled(int w, int h) => new(w, h, Enumerable.Range(1, w * h).SelectMany(n => new byte[] { (byte)n, 0, 0, 255 }).ToArray());
    private static byte[] Labels(RasterImage image) => image.Bgra.Where((_, i) => i % 4 == 0).ToArray();

    [Fact]
    public void RotationsAndFlips_PreserveExactPixelPositionsOnOddDimensions()
    {
        var source = Labeled(3, 2);
        var right = RasterOperations.Rotate90(source);
        Assert.Equal((2, 3), (right.Width, right.Height));
        Assert.Equal(new byte[] { 4, 1, 5, 2, 6, 3 }, Labels(right));
        Assert.Equal(new byte[] { 3, 6, 2, 5, 1, 4 }, Labels(RasterOperations.Rotate270(source)));
        Assert.Equal(new byte[] { 6, 5, 4, 3, 2, 1 }, Labels(RasterOperations.Rotate180(source)));
        Assert.Equal(new byte[] { 3, 2, 1, 6, 5, 4 }, Labels(RasterOperations.FlipHorizontal(source)));
        Assert.Equal(new byte[] { 4, 5, 6, 1, 2, 3 }, Labels(RasterOperations.FlipVertical(source)));
        Assert.Equal(source.Bgra, RasterOperations.Rotate270(right).Bgra);
    }
    [Fact]
    public void CropAndPad_KeepSourcePixelsAndAlpha()
    {
        var cropped = RasterOperations.Crop(Labeled(3, 3), new RasterRect(1, 1, 2, 2));
        Assert.Equal(new byte[] { 5, 6, 8, 9 }, Labels(cropped));
        var padded = RasterOperations.Pad(cropped, 5, 4, 1, 1, new BgraColor(10, 20, 30, 0));
        Assert.Equal(new byte[] { 10, 20, 30, 0 }, padded.Bgra[..4]);
        Assert.Equal(5, padded.Bgra[(5 + 1) * 4]);
        Assert.Equal(cropped.Bgra, RasterOperations.Crop(padded, new RasterRect(1, 1, 2, 2)).Bgra);
    }
    [Fact]
    public void Grayscale_UsesRec709AndPreservesAlpha()
    {
        var result = RasterOperations.Grayscale(new RasterImage(3, 1, new byte[] { 0, 0, 255, 17, 0, 255, 0, 128, 255, 0, 0, 0 }));
        Assert.Equal(new byte[] { 54, 54, 54, 17, 182, 182, 182, 128, 18, 18, 18, 0 }, result.Bgra);
    }
    [Fact]
    public void Compose_UsesStraightAlphaSourceOver()
    {
        var pixel = new RasterImage(1, 1, new byte[] { 0, 0, 255, 128 });
        var result = RasterOperations.Compose(new[] { (pixel, 0, 0) }, 2, 1, new BgraColor(255, 0, 0));
        Assert.Equal(new byte[] { 127, 0, 128, 255, 255, 0, 0, 255 }, result.Bgra);
        Assert.Equal(new byte[] { 0, 0, 255, 128 }, pixel.Bgra);
    }
    [Fact]
    public void BilinearResize_InterpolatesPremultipliedColorsWithoutTransparentHalos()
    {
        var source = new RasterImage(2, 1, new byte[] { 255, 0, 0, 0, 0, 0, 255, 255 });
        var result = RasterOperations.ResizeBilinear(source, 1, 1);
        Assert.Equal(new byte[] { 0, 0, 255, 128 }, result.Bgra);
        var opaque = Labeled(2, 1);
        Assert.Equal(opaque.Bgra, RasterOperations.ResizeBilinear(opaque, 2, 1).Bgra);
    }
    [Fact]
    public void Pixelate_ClipsLastBlockAndKeepsOutsideTheRectangle()
    {
        var source = Labeled(3, 2);
        var result = RasterOperations.Pixelate(source, new RasterRect(1, 0, 2, 2), 3);
        Assert.Equal(new byte[] { 1, 4, 4, 4, 4, 4 }, Labels(result));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, Labels(source));
    }
    [Fact]
    public void InvalidDimensionsAndRectangles_AreRejectedBeforeAllocation()
    {
        Assert.Throws<RasterException>(() => new RasterImage(int.MaxValue, int.MaxValue, Array.Empty<byte>()));
        Assert.Throws<RasterException>(() => RasterImage.ByteLength(60_000_001, 1));
        Assert.Throws<RasterException>(() => new RasterImage(2, 2, new byte[4]));
        Assert.Throws<RasterException>(() => RasterOperations.Crop(Labeled(2, 2), new(int.MaxValue, 0, 1, 1)));
        Assert.Throws<RasterException>(() => RasterOperations.Pad(Labeled(2, 2), 1, 1, 0, 0, default));
        Assert.Throws<RasterException>(() => RasterOperations.Pixelate(Labeled(2, 2), new(0, 0, 2, 2), 0));
        Assert.Throws<RasterException>(() => RasterOperations.Compose(new[] { (Labeled(2, 2), 1, 1) }, 2, 2, default));
    }
    [Fact]
    public void PixelWork_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => RasterOperations.ResizeBilinear(Labeled(2, 2), 2, 2, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => RasterOperations.Compose(new[] { (Labeled(1, 1), 0, 0) }, 1, 1, default, cts.Token));
    }
    [Fact]
    public void MultiplePixelationRegions_KeepOutsidePixelsAndValidateTheWholePlan()
    {
        var source = Labeled(4, 2);
        var result = RasterOperations.PixelateRegions(source, new[] { (new RasterRect(0, 0, 2, 1), 2), (new RasterRect(2, 1, 2, 1), 2) });
        Assert.Equal(new byte[] { 1, 1, 3, 4, 5, 6, 7, 7 }, Labels(result));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, Labels(source));
        Assert.Throws<RasterException>(() => RasterOperations.PixelateRegions(source, Enumerable.Repeat((new RasterRect(0, 0, 1, 1), 2), 1025).ToArray()));
        Assert.Throws<RasterException>(() => RasterOperations.PixelateRegions(source, new[] { (new RasterRect(0, 0, 1, 1), 2), (new RasterRect(4, 0, 1, 1), 2) }));
    }
}
