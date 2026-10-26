using System;
using Xunit;
using PowerTrove.Platform.Windows.Displays;
using Windows.Graphics;

namespace PowerTrove.Platform.Windows.Tests.Displays;

public class PlacementCalculatorTests
{
    [Fact]
    public void CalculateCenterPlacement_Standard1080p()
    {
        // Arrange
        var workArea = new MonitorWorkArea(0, 0, 1920, 1080, 96, 96);
        double width = 700;
        double height = 500;

        // Act
        var rect = PlacementCalculator.CalculateCenterPlacement(width, height, workArea);

        // Assert
        Assert.Equal(700, rect.Width);
        Assert.Equal(500, rect.Height);
        Assert.Equal((1920 - 700) / 2, rect.X); // 610
        Assert.Equal((1080 - 500) / 2, rect.Y); // 290
    }

    [Fact]
    public void CalculateCenterPlacement_NegativeCoordinates()
    {
        // Arrange (Monitor to the left of the primary monitor)
        var workArea = new MonitorWorkArea(-1920, 0, 1920, 1080, 96, 96);
        double width = 700;
        double height = 500;

        // Act
        var rect = PlacementCalculator.CalculateCenterPlacement(width, height, workArea);

        // Assert
        Assert.Equal(700, rect.Width);
        Assert.Equal(500, rect.Height);
        Assert.Equal(-1920 + (1920 - 700) / 2, rect.X); // -1920 + 610 = -1310
        Assert.Equal((1080 - 500) / 2, rect.Y);
    }

    [Fact]
    public void CalculateCenterPlacement_WithScaling()
    {
        // Arrange (150% scaling)
        var workArea = new MonitorWorkArea(0, 0, 1920, 1080, 144, 144);
        double width = 700;
        double height = 500;

        // Act
        var rect = PlacementCalculator.CalculateCenterPlacement(width, height, workArea);

        // Assert
        int expectedWidth = (int)(700 * 1.5); // 1050
        int expectedHeight = (int)(500 * 1.5); // 750
        Assert.Equal(expectedWidth, rect.Width);
        Assert.Equal(expectedHeight, rect.Height);
        Assert.Equal((1920 - expectedWidth) / 2, rect.X); // 435
        Assert.Equal((1080 - expectedHeight) / 2, rect.Y); // 165
    }

    [Fact]
    public void CalculateCenterPlacement_ClampsToWorkArea()
    {
        // Arrange (Small screen, e.g. 800x600, with 150% scaling)
        var workArea = new MonitorWorkArea(0, 0, 800, 600, 144, 144);
        double width = 700; // Physical width would be 1050
        double height = 500; // Physical height would be 750

        // Act
        var rect = PlacementCalculator.CalculateCenterPlacement(width, height, workArea);

        // Assert
        Assert.Equal(800, rect.Width); // Clamped to work area width
        Assert.Equal(600, rect.Height); // Clamped to work area height
        Assert.Equal(0, rect.X); // (800 - 800) / 2
        Assert.Equal(0, rect.Y); // (600 - 600) / 2
    }
}
