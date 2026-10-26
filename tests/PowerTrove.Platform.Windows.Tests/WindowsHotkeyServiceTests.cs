using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using PowerTrove.Core.Input;
using PowerTrove.Platform.Windows.Input;
using Moq;
using Xunit;

namespace PowerTrove.Platform.Windows.Tests;

public class WindowsHotkeyServiceTests
{
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    private static Mock<INativeHotkeyInterop> CreateInterop()
    {
        var interop = new Mock<INativeHotkeyInterop>();
        interop.Setup(x => x.CreateMessageWindow(It.IsAny<Action<int, IntPtr>>()))
            .Returns(new IntPtr(42));
        interop.Setup(x => x.DestroyMessageWindow()).Returns(true);
        interop.Setup(x => x.UnregisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>()))
            .Returns(true);
        return interop;
    }

    [Fact]
    public void Register_Success_ReturnsSuccess()
    {
        // Arrange
        var mockInterop = CreateInterop();
        mockInterop.Setup(x => x.RegisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<uint>(), It.IsAny<uint>()))
                   .Returns(true);

        using var service = new WindowsHotkeyService(mockInterop.Object);
        var config = new HotkeyConfiguration(KeyModifiers.Alt, 0x20);

        // Act
        var result = service.Register(config);

        // Assert
        Assert.Equal(RegisterHotkeyResult.Success, result);
        mockInterop.Verify(x => x.RegisterHotKey(It.IsAny<IntPtr>(), 1, It.IsAny<uint>(), 0x20), Times.Once);
    }

    [Fact]
    public void Register_Conflict_ReturnsConflict()
    {
        // Arrange
        var mockInterop = CreateInterop();
        mockInterop.Setup(x => x.RegisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<uint>(), It.IsAny<uint>()))
                   .Returns(false);
        mockInterop.Setup(x => x.GetLastError())
                   .Returns(ERROR_HOTKEY_ALREADY_REGISTERED);

        using var service = new WindowsHotkeyService(mockInterop.Object);
        var config = new HotkeyConfiguration(KeyModifiers.Alt, 0x20);

        // Act
        var result = service.Register(config);

        // Assert
        Assert.Equal(RegisterHotkeyResult.Conflict, result);
    }

    [Fact]
    public void Register_RepeatedSameConfig_DoesNotCallInteropTwice()
    {
        // Arrange
        var mockInterop = CreateInterop();
        mockInterop.Setup(x => x.RegisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<uint>(), It.IsAny<uint>()))
                   .Returns(true);

        using var service = new WindowsHotkeyService(mockInterop.Object);
        var config = new HotkeyConfiguration(KeyModifiers.Alt, 0x20);

        // Act
        service.Register(config);
        var result = service.Register(config);

        // Assert
        Assert.Equal(RegisterHotkeyResult.Success, result);
        mockInterop.Verify(x => x.RegisterHotKey(It.IsAny<IntPtr>(), 1, It.IsAny<uint>(), 0x20), Times.Once);
    }

    [Theory]
    [InlineData(ERROR_HOTKEY_ALREADY_REGISTERED, RegisterHotkeyResult.Conflict)]
    [InlineData(5, RegisterHotkeyResult.Error)]
    public void Register_ReplacementFails_KeepsOriginalShortcut(int error, RegisterHotkeyResult expected)
    {
        var interop = CreateInterop();
        var original = new HotkeyConfiguration(KeyModifiers.Alt, 0x20);
        interop.Setup(x => x.RegisterHotKey(new IntPtr(42), 1, 0x4001, 0x20)).Returns(true);
        interop.Setup(x => x.RegisterHotKey(new IntPtr(42), 2, 0x4002, 0x41)).Returns(false);
        interop.Setup(x => x.GetLastError()).Returns(error);
        using var service = new WindowsHotkeyService(interop.Object);
        Assert.Equal(RegisterHotkeyResult.Success, service.Register(original));

        Assert.Equal(expected, service.Register(new HotkeyConfiguration(KeyModifiers.Control, 0x41)));
        Assert.Equal(RegisterHotkeyResult.Success, service.Register(original));
        int events = 0;
        service.HotkeyTriggered += (_, _) => events++;
        service.ProcessMessage(0x0312, new IntPtr(1));
        Assert.Equal(1, events);
        interop.Verify(x => x.UnregisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>()), Times.Never);
        interop.Verify(x => x.RegisterHotKey(new IntPtr(42), 1, 0x4001, 0x20), Times.Once);
    }

    [Fact]
    public void Register_ReplacementSucceeds_ReservesBeforeReleasingAndRoutesNewIdentifier()
    {
        var interop = CreateInterop();
        bool candidateReserved = false, originalReleased = false;
        interop.Setup(x => x.RegisterHotKey(new IntPtr(42), 1, 0x4001, 0x20)).Returns(true);
        interop.Setup(x => x.RegisterHotKey(new IntPtr(42), 2, 0x4002, 0x41))
            .Callback(() =>
            {
                Assert.False(originalReleased);
                candidateReserved = true;
            }).Returns(true);
        interop.Setup(x => x.UnregisterHotKey(new IntPtr(42), 1))
            .Callback(() =>
            {
                Assert.True(candidateReserved);
                originalReleased = true;
            }).Returns(true);
        using var service = new WindowsHotkeyService(interop.Object);
        Assert.Equal(RegisterHotkeyResult.Success, service.Register(new HotkeyConfiguration(KeyModifiers.Alt, 0x20)));
        Assert.Equal(RegisterHotkeyResult.Success, service.Register(new HotkeyConfiguration(KeyModifiers.Control, 0x41)));

        int events = 0;
        service.HotkeyTriggered += (_, _) => events++;
        service.ProcessMessage(0x0312, new IntPtr(1));
        Assert.Equal(0, events);
        service.ProcessMessage(0x0312, new IntPtr(2));
        Assert.Equal(1, events);
        service.Unregister();
        interop.Verify(x => x.UnregisterHotKey(new IntPtr(42), 1), Times.Once);
        interop.Verify(x => x.UnregisterHotKey(new IntPtr(42), 2), Times.Once);
    }

    [Fact]
    public void Register_OriginalReleaseFails_RollsBackCandidateAndKeepsOriginal()
    {
        var interop = CreateInterop();
        interop.Setup(x => x.RegisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<uint>(), It.IsAny<uint>()))
            .Returns(true);
        interop.SetupSequence(x => x.UnregisterHotKey(new IntPtr(42), 1)).Returns(false).Returns(true);
        using var service = new WindowsHotkeyService(interop.Object);
        var original = new HotkeyConfiguration(KeyModifiers.Alt, 0x20);
        Assert.Equal(RegisterHotkeyResult.Success, service.Register(original));

        Assert.Equal(RegisterHotkeyResult.Error, service.Register(new HotkeyConfiguration(KeyModifiers.Control, 0x41)));
        Assert.Equal(RegisterHotkeyResult.Success, service.Register(original));
        interop.Verify(x => x.UnregisterHotKey(new IntPtr(42), 2), Times.Once);
        service.Unregister();
        interop.Verify(x => x.UnregisterHotKey(new IntPtr(42), 1), Times.Exactly(2));
    }

    [Fact]
    public void Register_RollbackReleaseFails_TracksCandidateAndBlocksFurtherReservationsUntilCleanup()
    {
        var interop = CreateInterop();
        interop.Setup(x => x.RegisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<uint>(), It.IsAny<uint>()))
            .Returns(true);
        interop.SetupSequence(x => x.UnregisterHotKey(new IntPtr(42), 1)).Returns(false).Returns(true);
        interop.SetupSequence(x => x.UnregisterHotKey(new IntPtr(42), 2)).Returns(false).Returns(false).Returns(true);
        using var service = new WindowsHotkeyService(interop.Object);
        Assert.Equal(RegisterHotkeyResult.Success, service.Register(new HotkeyConfiguration(KeyModifiers.Alt, 0x20)));

        Assert.Equal(RegisterHotkeyResult.Error, service.Register(new HotkeyConfiguration(KeyModifiers.Control, 0x41)));
        Assert.Equal(RegisterHotkeyResult.Error, service.Register(new HotkeyConfiguration(KeyModifiers.Control, 0x42)));
        interop.Verify(x => x.RegisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<uint>(), It.IsAny<uint>()),
            Times.Exactly(2));
        int events = 0;
        service.HotkeyTriggered += (_, _) => events++;
        service.ProcessMessage(0x0312, new IntPtr(2));
        Assert.Equal(0, events);
        service.ProcessMessage(0x0312, new IntPtr(1));
        Assert.Equal(1, events);
        service.Unregister();
        interop.Verify(x => x.UnregisterHotKey(new IntPtr(42), 2), Times.Exactly(3));
        interop.Verify(x => x.UnregisterHotKey(new IntPtr(42), 1), Times.Exactly(2));
    }

    [Fact]
    public void Unregister_Fails_ReportsErrorAndRetainsRegistrationForRetry()
    {
        var interop = CreateInterop();
        interop.Setup(x => x.RegisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<uint>(), It.IsAny<uint>()))
            .Returns(true);
        interop.SetupSequence(x => x.UnregisterHotKey(new IntPtr(42), 1)).Returns(false).Returns(true);
        interop.Setup(x => x.GetLastError()).Returns(5);
        using var service = new WindowsHotkeyService(interop.Object);
        var config = new HotkeyConfiguration(KeyModifiers.Alt, 0x20);
        Assert.Equal(RegisterHotkeyResult.Success, service.Register(config));

        Assert.Contains("Windows error 5", Assert.Throws<InvalidOperationException>(() => service.Unregister()).Message);
        Assert.Equal(RegisterHotkeyResult.Success, service.Register(config));
        interop.Verify(x => x.RegisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<uint>(), It.IsAny<uint>()), Times.Once);
        service.Unregister();
        interop.Verify(x => x.UnregisterHotKey(new IntPtr(42), 1), Times.Exactly(2));
        interop.Verify(x => x.DestroyMessageWindow(), Times.Never);
    }

    [Fact]
    public void Dispose_ReleaseFails_RetainsWindowAndAllowsRetry()
    {
        var interop = CreateInterop();
        interop.Setup(x => x.RegisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<uint>(), It.IsAny<uint>()))
            .Returns(true);
        interop.SetupSequence(x => x.UnregisterHotKey(new IntPtr(42), 1)).Returns(false).Returns(true);
        var service = new WindowsHotkeyService(interop.Object);
        Assert.Equal(RegisterHotkeyResult.Success, service.Register(new HotkeyConfiguration(KeyModifiers.Alt, 0x20)));

        Assert.Throws<InvalidOperationException>(() => service.Dispose());
        interop.Verify(x => x.DestroyMessageWindow(), Times.Never);
        service.Dispose();
        interop.Verify(x => x.UnregisterHotKey(new IntPtr(42), 1), Times.Exactly(2));
        interop.Verify(x => x.DestroyMessageWindow(), Times.Once);
    }

    [Fact]
    public void Unregister_WhenRegistered_CallsInteropUnregister()
    {
        // Arrange
        var mockInterop = CreateInterop();
        mockInterop.Setup(x => x.RegisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<uint>(), It.IsAny<uint>()))
                   .Returns(true);

        using var service = new WindowsHotkeyService(mockInterop.Object);
        var config = new HotkeyConfiguration(KeyModifiers.Alt, 0x20);
        service.Register(config);

        // Act
        service.Unregister();

        // Assert
        mockInterop.Verify(x => x.UnregisterHotKey(It.IsAny<IntPtr>(), 1), Times.Once);
    }

    [Fact]
    public void Dispose_WhenRegistered_CallsUnregister()
    {
        // Arrange
        var mockInterop = CreateInterop();
        mockInterop.Setup(x => x.RegisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<uint>(), It.IsAny<uint>()))
                   .Returns(true);

        var service = new WindowsHotkeyService(mockInterop.Object);
        var config = new HotkeyConfiguration(KeyModifiers.Alt, 0x20);
        service.Register(config);

        // Act
        service.Dispose();

        // Assert
        mockInterop.Verify(x => x.UnregisterHotKey(It.IsAny<IntPtr>(), 1), Times.Once);
    }

    [Fact]
    public void ProcessMessage_WhenWM_HOTKEY_RaisesEvent()
    {
        // Arrange
        var mockInterop = CreateInterop();
        using var service = new WindowsHotkeyService(mockInterop.Object);

        bool eventRaised = false;
        service.HotkeyTriggered += (s, e) => eventRaised = true;

        // Act
        service.ProcessMessage(0x0312 /* WM_HOTKEY */, new IntPtr(1));

        // Assert
        Assert.True(eventRaised);
    }

    [Fact]
    public void Register_WindowCreationFailed_DoesNotReserveThreadHotkey()
    {
        var interop = CreateInterop();
        interop.Setup(x => x.CreateMessageWindow(It.IsAny<Action<int, IntPtr>>()))
            .Returns(IntPtr.Zero);
        using var service = new WindowsHotkeyService(interop.Object);

        Assert.Equal(RegisterHotkeyResult.InitializationFailed,
            service.Register(new HotkeyConfiguration(KeyModifiers.Alt, 0x20)));
        interop.Verify(x => x.RegisterHotKey(It.IsAny<IntPtr>(), It.IsAny<int>(),
            It.IsAny<uint>(), It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public void Register_UsesCreatedMessageWindow()
    {
        var interop = CreateInterop();
        interop.Setup(x => x.RegisterHotKey(new IntPtr(42), 1, 0x4001, 0x20)).Returns(true);
        using var service = new WindowsHotkeyService(interop.Object);

        Assert.Equal(RegisterHotkeyResult.Success,
            service.Register(new HotkeyConfiguration(KeyModifiers.Alt, 0x20)));
    }

    [Fact]
    public void Dispose_Repeated_ReleasesWindowOnceAndRejectsRegistration()
    {
        var interop = CreateInterop();
        var service = new WindowsHotkeyService(interop.Object);
        bool triggered = false;
        service.HotkeyTriggered += (_, _) => triggered = true;

        service.Dispose();
        service.Dispose();
        service.ProcessMessage(0x0312, new IntPtr(1));

        Assert.False(triggered);
        Assert.Throws<ObjectDisposedException>(() =>
            service.Register(new HotkeyConfiguration(KeyModifiers.Alt, 0x20)));
        interop.Verify(x => x.DestroyMessageWindow(), Times.Once);
    }

    [Fact]
    public void Dispose_WindowReleaseFailed_AllowsRetry()
    {
        var interop = CreateInterop();
        interop.SetupSequence(x => x.DestroyMessageWindow()).Returns(false).Returns(true);
        var service = new WindowsHotkeyService(interop.Object);

        Assert.Throws<InvalidOperationException>(() => service.Dispose());
        service.Dispose();
        interop.Verify(x => x.DestroyMessageWindow(), Times.Exactly(2));
    }

    [Fact]
    public void NativeMessageWindows_RouteToTheirOwnersAndSurviveOtherOwnerDisposal()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var first = new WindowsHotkeyService();
        using var second = new WindowsHotkeyService();
        var handleField = typeof(WindowsHotkeyService).GetField("_hwnd",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var firstWindow = (IntPtr)handleField.GetValue(first)!;
        var secondWindow = (IntPtr)handleField.GetValue(second)!;
        Assert.NotEqual(IntPtr.Zero, firstWindow);
        Assert.NotEqual(IntPtr.Zero, secondWindow);
        var firstClass = new StringBuilder(256);
        var secondClass = new StringBuilder(256);
        Assert.True(GetClassName(firstWindow, firstClass, firstClass.Capacity) > 0);
        Assert.True(GetClassName(secondWindow, secondClass, secondClass.Capacity) > 0);
        Assert.NotEqual(firstClass.ToString(), secondClass.ToString());
        Assert.True(IsClassRegistered(firstClass.ToString()));
        Assert.True(IsClassRegistered(secondClass.ToString()));

        int firstEvents = 0, secondEvents = 0;
        first.HotkeyTriggered += (_, _) => firstEvents++;
        second.HotkeyTriggered += (_, _) => secondEvents++;
        SendMessage(firstWindow, 0x0312, new IntPtr(1), IntPtr.Zero);
        SendMessage(secondWindow, 0x0312, new IntPtr(1), IntPtr.Zero);
        Assert.Equal(1, firstEvents);
        Assert.Equal(1, secondEvents);

        first.Dispose();
        Assert.False(IsWindow(firstWindow));
        Assert.False(IsClassRegistered(firstClass.ToString()));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        SendMessage(secondWindow, 0x0312, new IntPtr(1), IntPtr.Zero);
        Assert.Equal(1, firstEvents);
        Assert.Equal(2, secondEvents);
        second.Dispose();
        Assert.False(IsWindow(secondWindow));
        Assert.False(IsClassRegistered(secondClass.ToString()));
    }

    private static bool IsClassRegistered(string className)
    {
        // WNDCLASS fits in 128 bytes on both x86 and x64. The returned string
        // pointers belong to Windows; only this structure buffer is ours to free.
        var buffer = Marshal.AllocHGlobal(128);
        try
        {
            return GetClassInfo(GetModuleHandle(IntPtr.Zero), className, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetClassInfo(IntPtr instance, string className, IntPtr windowClass);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(IntPtr moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
}
