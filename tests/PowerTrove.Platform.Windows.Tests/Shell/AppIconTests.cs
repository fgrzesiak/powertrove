using PowerTrove.Platform.Windows.Shell;
using Xunit;

namespace PowerTrove.Platform.Windows.Tests.Shell;

public class AppIconTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void Pixels_DrawMarkWithTransparentCounterAndPremultipliedEdges(int size)
    {
        byte[] pixels = AppIcon.Pixels(size);
        Assert.Equal(size * size * 4, pixels.Length);
        int At(int x, int y) => ((y * size / 128) * size + x * size / 128) * 4;
        Assert.Equal(0, pixels[3]);
        Assert.Equal(0, pixels[At(78, 52) + 3]); // arrow counter remains transparent
        int stem = At(36, 100);
        Assert.Equal(255, pixels[stem + 3]);
        Assert.True(pixels[stem] > pixels[stem + 2]);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.True(pixels[i] <= pixels[i + 3]);
            Assert.True(pixels[i + 1] <= pixels[i + 3]);
            Assert.True(pixels[i + 2] <= pixels[i + 3]);
        }
    }

    [Fact]
    public void Pixels_RejectsUnsupportedSizes()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => AppIcon.Pixels(8));
    }
}
