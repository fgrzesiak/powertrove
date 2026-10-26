using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Core;
using PowerTrove.Platform.Windows.Capture;
using Xunit;

namespace PowerTrove.Platform.Windows.Tests.Capture;

public class ExplorerContextProviderTests
{
    private class MockShellInterop : IShellInterop
    {
        public bool ShouldSucceed { get; set; } = true;
        public string? ErrorMessage { get; set; }
        public bool ThrowOnCapture { get; set; }
        public List<SelectedFile> FilesToReturn { get; set; } = new();

        public bool TryGetSelectedFiles(nint hwnd, out IReadOnlyList<SelectedFile> files, out string? error)
        {
            if (ThrowOnCapture) throw new IOException("Synthetic secret: C:\\private\\records.csv");
            if (ShouldSucceed)
            {
                files = FilesToReturn;
                error = null;
                return true;
            }
            else
            {
                files = Array.Empty<SelectedFile>();
                error = ErrorMessage;
                return false;
            }
        }
    }

    [Fact]
    public async Task CanCaptureAsync_ReturnsTrue_ForExplorerProcess()
    {
        var provider = new ExplorerContextProvider(new MockShellInterop(), TimeSpan.FromSeconds(5));
        var source = new SourceWindowIdentity(1234, 100, "explorer", DateTimeOffset.UtcNow);

        bool result = await provider.CanCaptureAsync(source, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task CanCaptureAsync_ReturnsFalse_ForNonExplorerProcess()
    {
        var provider = new ExplorerContextProvider(new MockShellInterop(), TimeSpan.FromSeconds(5));
        var source = new SourceWindowIdentity(1234, 100, "notepad", DateTimeOffset.UtcNow);

        bool result = await provider.CanCaptureAsync(source, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsAvailable_WhenFilesAreSelected()
    {
        var mockInterop = new MockShellInterop
        {
            ShouldSucceed = true,
            FilesToReturn = new List<SelectedFile>
            {
                new SelectedFile("C:\\test.txt", "test.txt", ".txt", null, 100, true, true, null, false)
            }
        };

        var provider = new ExplorerContextProvider(mockInterop, TimeSpan.FromSeconds(5));
        var source = new SourceWindowIdentity(1234, 100, "explorer", DateTimeOffset.UtcNow);
        var invocationId = Guid.NewGuid();

        var snapshot = await provider.CaptureAsync(invocationId, source, CancellationToken.None);

        Assert.Equal(ContextStatus.Available, snapshot.Status);
        Assert.IsType<FileSelection>(snapshot.Content);
        var fileSelection = (FileSelection)snapshot.Content;
        Assert.Single(fileSelection.Files);
        Assert.Equal("C:\\test.txt", fileSelection.Files[0].Path);
        Assert.Contains("shell.selection.read.v1", snapshot.ProvidedCapabilities!);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsEmpty_WhenNoFilesAreSelected()
    {
        var mockInterop = new MockShellInterop
        {
            ShouldSucceed = true,
            FilesToReturn = new List<SelectedFile>()
        };

        var provider = new ExplorerContextProvider(mockInterop, TimeSpan.FromSeconds(5));
        var source = new SourceWindowIdentity(1234, 100, "explorer", DateTimeOffset.UtcNow);
        var invocationId = Guid.NewGuid();

        var snapshot = await provider.CaptureAsync(invocationId, source, CancellationToken.None);

        Assert.Equal(ContextStatus.Empty, snapshot.Status);
        Assert.IsType<NoContent>(snapshot.Content);
        Assert.Equal("No files or folders are currently selected.", snapshot.UserMessage);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsUnsupported_WhenInteropFails()
    {
        var mockInterop = new MockShellInterop
        {
            ShouldSucceed = false,
            ErrorMessage = "Synthetic secret: C:\\private\\records.csv"
        };

        var provider = new ExplorerContextProvider(mockInterop, TimeSpan.FromSeconds(5));
        var source = new SourceWindowIdentity(1234, 100, "explorer", DateTimeOffset.UtcNow);
        var invocationId = Guid.NewGuid();

        var snapshot = await provider.CaptureAsync(invocationId, source, CancellationToken.None);

        Assert.Equal(ContextStatus.Unsupported, snapshot.Status);
        Assert.IsType<NoContent>(snapshot.Content);
        Assert.Equal(PowerTrove.Platform.Windows.Resources.Strings.ExplorerUnsupportedView, snapshot.UserMessage);
        Assert.DoesNotContain("private", snapshot.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureAsync_NeverDisplaysExceptionContainingPrivateFileNames()
    {
        var provider = new ExplorerContextProvider(new MockShellInterop { ThrowOnCapture = true }, TimeSpan.FromSeconds(5));
        var source = new SourceWindowIdentity(1234, 100, "explorer", DateTimeOffset.UtcNow);
        var snapshot = await provider.CaptureAsync(Guid.NewGuid(), source, CancellationToken.None);
        Assert.Equal(ContextStatus.Failed, snapshot.Status);
        Assert.Equal(PowerTrove.Platform.Windows.Resources.Strings.ExplorerError, snapshot.UserMessage);
        Assert.DoesNotContain("records.csv", snapshot.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    private class HangingShellInterop : IShellInterop
    {
        private readonly TaskCompletionSource<Thread> _entered;
        private readonly ManualResetEventSlim _release;

        public HangingShellInterop(TaskCompletionSource<Thread> entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public bool TryGetSelectedFiles(nint hwnd, out IReadOnlyList<SelectedFile> files, out string? error)
        {
            _entered.TrySetResult(Thread.CurrentThread);
            _release.Wait(TimeSpan.FromSeconds(10)); // Simulate a COM server ignoring cancellation.
            files = Array.Empty<SelectedFile>();
            error = null;
            return true;
        }
    }

    [Fact]
    public async Task CaptureAsync_ReturnsTimedOut_WhenShellInteropHangs()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);

        var interop = new HangingShellInterop(entered, release);
        var provider = new ExplorerContextProvider(interop, TimeSpan.FromMilliseconds(250)); // Short timeout for the test
        var source = new SourceWindowIdentity(1234, 100, "explorer", DateTimeOffset.UtcNow);
        var invocationId = Guid.NewGuid();

        Thread? worker = null;
        try
        {
            var captureTask = provider.CaptureAsync(invocationId, source, CancellationToken.None).AsTask();
            worker = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var snapshot = await captureTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(ContextStatus.TimedOut, snapshot.Status);
            Assert.IsType<NoContent>(snapshot.Content);
            Assert.Equal("Explorer capture timed out.", snapshot.UserMessage);

            // Verify another call immediately gets TimedOut status due to busy slot
            var busySnapshot = await provider.CaptureAsync(Guid.NewGuid(), source, CancellationToken.None);
            Assert.Equal(ContextStatus.TimedOut, busySnapshot.Status);
            Assert.Equal("Explorer capture is still busy.", busySnapshot.UserMessage);
        }
        finally
        {
            release.Set();
            worker ??= await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await Task.Run(() => worker.Join(TimeSpan.FromSeconds(5))));
        }

        // Verify slot is reusable after worker cleanup
        var retrySnapshot = await provider.CaptureAsync(Guid.NewGuid(), source, CancellationToken.None);
        Assert.Equal(ContextStatus.Empty, retrySnapshot.Status); // HangingShellInterop returns an empty list, which yields Empty status
    }
}
