using PowerTrove.Platform.Windows.Displays;
using Xunit;

namespace PowerTrove.Platform.Windows.Tests.Displays;

public class LauncherPlacementTests
{
    private static readonly MonitorWorkArea Fhd = new(0, 0, 1920, 1040, 96, 96);

    [Fact]
    public void Launcher_IsCenteredHorizontallyInTheUpperFifth()
    {
        var rect = PlacementCalculator.CalculateLauncherPlacement(700, 500, Fhd);

        Assert.Equal((1920 - 700) / 2, rect.X);
        Assert.Equal(208, rect.Y);
        Assert.Equal(700, rect.Width);
    }

    [Fact]
    public void Launcher_StaysInsideSmallAndOffsetWorkAreas()
    {
        var small = new MonitorWorkArea(-1280, 100, 1280, 600, 144, 144);

        var rect = PlacementCalculator.CalculateLauncherPlacement(700, 500, small);

        Assert.True(rect.Y >= small.Y && rect.Y + rect.Height <= small.Y + small.Height);
        Assert.True(rect.X >= small.X && rect.X + rect.Width <= small.X + small.Width);
    }

    [Theory]
    [InlineData(-500, -500, 0, 0)]
    [InlineData(5000, 5000, 1920 - 700, 1040 - 500)]
    [InlineData(100, 120, 100, 120)]
    public void RememberedPosition_IsClampedToTheWorkArea(int x, int y, int expectedX, int expectedY)
    {
        var rect = PlacementCalculator.ClampToWorkArea(x, y, 700, 500, Fhd);

        Assert.Equal(expectedX, rect.X);
        Assert.Equal(expectedY, rect.Y);
    }
    [Theory]
    [InlineData(-1920, 100, 1920, 1040, 96)]
    [InlineData(1920, -900, 1280, 720, 192)]
    [InlineData(0, 0, 800, 560, 144)]
    public void Launcher_IsPredictableAndLeavesAScaledMargin(int x, int y, int width, int height, int dpi)
    {
        var area = new MonitorWorkArea(x, y, width, height, dpi, dpi);
        var first = PlacementCalculator.CalculateLauncherPlacement(700, 500, area);
        var again = PlacementCalculator.CalculateLauncherPlacement(700, 500, area);
        int margin = (int)Math.Round(12.0 * dpi / 96);
        Assert.Equal(first, again);
        Assert.Equal(x + (width - first.Width) / 2, first.X);
        Assert.True(first.X >= x + margin && first.X + first.Width <= x + width - margin);
        Assert.True(first.Y >= y + margin && first.Y + first.Height <= y + height - margin);
    }
}
