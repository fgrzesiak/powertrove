using ContextBar.Pack.Pdf;

namespace ContextBar.Pack.Pdf.Tests;

public class BlankPageDetectorTests
{
    private static PageRaster CreateRaster(int width, int height, double whiteRatio)
    {
        byte[] pixels = new byte[width * height];
        int whiteCount = (int)(pixels.Length * whiteRatio);

        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = i < whiteCount ? (byte)255 : (byte)0;
        }

        return new PageRaster(width, height, pixels);
    }

    [Fact]
    public void IsBlank_Strict_999White_ReturnsTrue()
    {
        var raster = CreateRaster(100, 100, 0.999);
        Assert.True(BlankPageDetector.IsBlank(raster, "strict"));
    }

    [Fact]
    public void IsBlank_Strict_998White_ReturnsFalse()
    {
        var raster = CreateRaster(100, 100, 0.998);
        Assert.False(BlankPageDetector.IsBlank(raster, "strict"));
    }

    [Fact]
    public void IsBlank_Normal_995White_ReturnsTrue()
    {
        var raster = CreateRaster(100, 100, 0.995);
        Assert.True(BlankPageDetector.IsBlank(raster, "normal"));
    }

    [Fact]
    public void IsBlank_Normal_994White_ReturnsFalse()
    {
        var raster = CreateRaster(100, 100, 0.994);
        Assert.False(BlankPageDetector.IsBlank(raster, "normal"));
    }

    [Fact]
    public void IsBlank_Relaxed_985White_ReturnsTrue()
    {
        var raster = CreateRaster(100, 100, 0.985);
        Assert.True(BlankPageDetector.IsBlank(raster, "relaxed"));
    }

    [Fact]
    public void IsBlank_Relaxed_984White_ReturnsFalse()
    {
        var raster = CreateRaster(100, 100, 0.984);
        Assert.False(BlankPageDetector.IsBlank(raster, "relaxed"));
    }

    [Fact]
    public void IsBlank_Default_ActsAsNormal()
    {
        var raster = CreateRaster(100, 100, 0.995);
        Assert.True(BlankPageDetector.IsBlank(raster, "unknown"));

        var raster2 = CreateRaster(100, 100, 0.994);
        Assert.False(BlankPageDetector.IsBlank(raster2, "unknown"));
    }
}
