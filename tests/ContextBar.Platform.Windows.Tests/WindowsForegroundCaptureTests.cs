using System;
using System.Text;
using System.Threading;
using Xunit.Abstractions;
using System.Threading.Tasks;
using ContextBar.Core;
using ContextBar.Platform.Windows.Capture;
using Xunit;

namespace ContextBar.Platform.Windows.Tests;

public class WindowsForegroundCaptureTests
{
    private readonly ITestOutputHelper _output;

    public WindowsForegroundCaptureTests(ITestOutputHelper output) => _output = output;

    private class MockNativeInterop : INativeWindowInterop
    {
        public nint ForegroundWindowToReturn { get; set; } = 12345;
        public int ProcessIdToReturn { get; set; } = 999;
        public int? ProcessIdOnRecheck { get; set; }
        private int _windowThreadLookupCount;
        public int ThreadIdToReturn { get; set; } = 888;
        public string? ProcessNameToReturn { get; set; } = "explorer.exe";
        public string? WindowTitleToReturn { get; set; } = "Documents";
        public int CurrentProcessIdToReturn { get; set; } = 100;

        public bool ThrowUnauthorizedAccess { get; set; } = false;

        public nint GetForegroundWindow()
        {
            if (ThrowUnauthorizedAccess)
            {
                throw new UnauthorizedAccessException();
            }
            return ForegroundWindowToReturn;
        }

        public int GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId)
        {
            _windowThreadLookupCount++;
            lpdwProcessId = _windowThreadLookupCount > 1 && ProcessIdOnRecheck.HasValue
                ? ProcessIdOnRecheck.Value
                : ProcessIdToReturn;
            return ThreadIdToReturn;
        }

        public int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount)
        {
            if (WindowTitleToReturn != null)
            {
                lpString.Clear();
                lpString.Append(WindowTitleToReturn);
                return WindowTitleToReturn.Length;
            }
            return 0;
        }

        public int GetWindowTextLength(nint hWnd)
        {
            return WindowTitleToReturn?.Length ?? 0;
        }

        public string? GetProcessNameById(int processId)
        {
            return ProcessNameToReturn;
        }

        public int GetCurrentProcessId()
        {
            return CurrentProcessIdToReturn;
        }
    }

    [Fact]
    public async Task CaptureForegroundAsync_ValidWindow_ReturnsIdentity()
    {
        // Arrange
        var interop = new MockNativeInterop();
        var capture = new WindowsForegroundCapture(interop);

        // Act
        var result = await capture.CaptureForegroundAsync();

        // Assert
        Assert.Equal(ForegroundCaptureError.None, result.Error);
        Assert.NotNull(result.Identity);
        Assert.Equal(12345, result.Identity.WindowHandle);
        Assert.Equal(999, result.Identity.ProcessId);
        Assert.Equal("explorer.exe", result.Identity.ProcessName);
        Assert.Equal("Documents", result.Identity.Title);
        Assert.True(result.Identity.CapturedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task CaptureForegroundAsync_WindowVanished_ReturnsError()
    {
        // Arrange
        var interop = new MockNativeInterop
        {
            ForegroundWindowToReturn = IntPtr.Zero
        };
        var capture = new WindowsForegroundCapture(interop);

        // Act
        var result = await capture.CaptureForegroundAsync();

        // Assert
        Assert.Equal(ForegroundCaptureError.WindowVanished, result.Error);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task CaptureForegroundAsync_AccessDenied_ReturnsError()
    {
        // Arrange
        var interop = new MockNativeInterop
        {
            ThrowUnauthorizedAccess = true
        };
        var capture = new WindowsForegroundCapture(interop);

        // Act
        var result = await capture.CaptureForegroundAsync();

        // Assert
        Assert.Equal(ForegroundCaptureError.AccessDenied, result.Error);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task CaptureForegroundAsync_SourceChangesDuringCapture_ReturnsWindowVanished()
    {
        var interop = new MockNativeInterop { ProcessIdOnRecheck = 42 };
        var result = await new WindowsForegroundCapture(interop).CaptureForegroundAsync();
        Assert.Equal(ForegroundCaptureError.WindowVanished, result.Error);
        Assert.Null(result.Identity);
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public async Task CaptureForegroundAsync_RealWindowsManualSmokeTest()
    {
        // Opt-in only, since CI runners may have no meaningful interactive desktop.
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("CB_RUN_WINDOWS_INTEGRATION") != "1")
            return;

        var result = await new WindowsForegroundCapture().CaptureForegroundAsync();
        _output.WriteLine($"Foreground capture result: {result.Error}; process: {result.Identity?.ProcessName ?? "(unavailable)"}");

        // Failure is explicit; a successful capture must never return a zero HWND/PID.
        if (result.Error == ForegroundCaptureError.None)
        {
            Assert.NotNull(result.Identity);
            Assert.NotEqual(IntPtr.Zero, result.Identity.WindowHandle);
            Assert.True(result.Identity.ProcessId > 0);
        }
        else
        {
            Assert.Null(result.Identity);
        }
    }

    [Fact]
    public async Task CaptureForegroundAsync_SelfCapture_ReturnsPreventedError()
    {
        // Arrange
        var interop = new MockNativeInterop
        {
            CurrentProcessIdToReturn = 999,
            ProcessIdToReturn = 999
        };
        var capture = new WindowsForegroundCapture(interop);

        // Act
        var result = await capture.CaptureForegroundAsync();

        // Assert
        Assert.Equal(ForegroundCaptureError.SelfCapturePrevented, result.Error);
        Assert.Null(result.Identity);
    }
}
