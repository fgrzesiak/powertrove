using PowerTrove.Platform.Windows.Shell;
using Xunit;

namespace PowerTrove.Platform.Windows.Tests.Shell;

public class AppIconTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void Pixels_DrawAnOpaqueTileWithTransparentCorners(int size)
    {
        byte[] pixels = AppIcon.Pixels(size);

        Assert.Equal(size * size * 4, pixels.Length);
        Assert.Equal(0, pixels[3]); // top-left corner is outside the rounded tile
        int center = (size / 2 * size + size / 2) * 4;
        Assert.Equal(255, pixels[center + 3]);
        int edge = (size / 2 * size + size / 16) * 4; // left edge of the tile, outside the bar
        Assert.Equal(255, pixels[edge + 3]);
        Assert.True(pixels[edge] > pixels[edge + 2], "Tile is blue (B > R).");
    }

    [Fact]
    public void Pixels_RejectsUnsupportedSizes()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => AppIcon.Pixels(8));
    }
}
