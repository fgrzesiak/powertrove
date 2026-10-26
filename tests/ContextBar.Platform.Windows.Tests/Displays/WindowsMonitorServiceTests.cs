using System;
using Moq;
using Xunit;
using ContextBar.Platform.Windows.Displays;

namespace ContextBar.Platform.Windows.Tests.Displays;

public class WindowsMonitorServiceTests
{
    [Fact]
    public void GetMonitorWorkAreaForWindow_ReturnsPrimary_WhenWindowVanishes()
    {
        // Arrange
        var mockInterop = new Mock<INativeMonitorInterop>();
        mockInterop.Setup(x => x.MonitorFromWindow(It.IsAny<nint>(), MonitorOptions.MONITOR_DEFAULTTONEAREST))
                   .Returns(IntPtr.Zero);

        // Primary fallback setup
        nint primaryMonitor = (nint)2;
        mockInterop.Setup(x => x.MonitorFromWindow(IntPtr.Zero, MonitorOptions.MONITOR_DEFAULTTOPRIMARY))
                   .Returns(primaryMonitor);

        var primaryInfo = new MONITORINFO
        {
            cbSize = 40,
            rcWork = new RECT { left = 0, top = 0, right = 1920, bottom = 1080 }
        };
        mockInterop.Setup(x => x.GetMonitorInfo(primaryMonitor, out primaryInfo)).Returns(true);

        uint dpiX = 96, dpiY = 96;
        mockInterop.Setup(x => x.GetDpiForMonitor(primaryMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out dpiX, out dpiY));

        var service = new WindowsMonitorService(mockInterop.Object);

        // Act
        var result = service.GetMonitorWorkAreaForWindow((nint)12345);

        // Assert
        Assert.Equal(0, result.X);
        Assert.Equal(0, result.Y);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.Equal(96, result.DpiX);
    }

    [Fact]
    public void GetMonitorWorkAreaForWindow_HandlesNegativeCoordinates()
    {
        // Arrange
        var mockInterop = new Mock<INativeMonitorInterop>();
        nint hMonitor = (nint)1;
        mockInterop.Setup(x => x.MonitorFromWindow(It.IsAny<nint>(), MonitorOptions.MONITOR_DEFAULTTONEAREST))
                   .Returns(hMonitor);

        var monitorInfo = new MONITORINFO
        {
            cbSize = 40,
            rcWork = new RECT { left = -1920, top = -100, right = 0, bottom = 980 }
        };
        mockInterop.Setup(x => x.GetMonitorInfo(hMonitor, out monitorInfo)).Returns(true);

        uint dpiX = 144, dpiY = 144; // 150% scaling
        mockInterop.Setup(x => x.GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out dpiX, out dpiY));

        var service = new WindowsMonitorService(mockInterop.Object);

        // Act
        var result = service.GetMonitorWorkAreaForWindow((nint)12345);

        // Assert
        Assert.Equal(-1920, result.X);
        Assert.Equal(-100, result.Y);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.Equal(144, result.DpiX);
        Assert.Equal(1.5, result.ScaleFactorX);
    }

    [Fact]
    public void GetMonitorWorkAreaForWindow_AbsoluteFallback_WhenPrimaryFails()
    {
        // Arrange
        var mockInterop = new Mock<INativeMonitorInterop>();
        mockInterop.Setup(x => x.MonitorFromWindow(It.IsAny<nint>(), It.IsAny<MonitorOptions>()))
                   .Returns(IntPtr.Zero);

        var service = new WindowsMonitorService(mockInterop.Object);

        // Act
        var result = service.GetMonitorWorkAreaForWindow((nint)12345);

        // Assert
        Assert.Equal(0, result.X);
        Assert.Equal(0, result.Y);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.Equal(96, result.DpiX);
    }
}
