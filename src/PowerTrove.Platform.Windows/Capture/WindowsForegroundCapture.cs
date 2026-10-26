using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Core;

namespace PowerTrove.Platform.Windows.Capture;

public sealed class WindowsForegroundCapture : IForegroundWindowCapture
{
    private readonly INativeWindowInterop _interop;

    public WindowsForegroundCapture() : this(new WindowsNativeInterop())
    {
    }

    public WindowsForegroundCapture(INativeWindowInterop interop)
    {
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
    }

    public ValueTask<ForegroundCaptureResult> CaptureForegroundAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ValueTask<ForegroundCaptureResult>(Task.FromCanceled<ForegroundCaptureResult>(cancellationToken));
        }

        try
        {
            nint hWnd = _interop.GetForegroundWindow();

            if (hWnd == IntPtr.Zero)
            {
                return new ValueTask<ForegroundCaptureResult>(new ForegroundCaptureResult(null, ForegroundCaptureError.WindowVanished));
            }

            int threadId = _interop.GetWindowThreadProcessId(hWnd, out int processId);
            if (threadId == 0 || processId == 0)
            {
                // Can happen if the window is destroyed immediately after getting the handle
                return new ValueTask<ForegroundCaptureResult>(new ForegroundCaptureResult(null, ForegroundCaptureError.WindowVanished));
            }

            int currentProcessId = _interop.GetCurrentProcessId();
            if (processId == currentProcessId)
            {
                // We should not capture ourselves. If this happens, it might mean the palette opened before capture was completed.
                return new ValueTask<ForegroundCaptureResult>(new ForegroundCaptureResult(null, ForegroundCaptureError.SelfCapturePrevented));
            }

            string? processName = _interop.GetProcessNameById(processId);

            int titleLength = _interop.GetWindowTextLength(hWnd);
            string? windowTitle = null;

            if (titleLength > 0)
            {
                var sb = new StringBuilder(titleLength + 1);
                int lengthCopied = _interop.GetWindowText(hWnd, sb, sb.Capacity);
                if (lengthCopied > 0)
                {
                    windowTitle = sb.ToString();
                }
            }

            // The source HWND may have closed or been reassigned while its
            // process name/window caption was being retrieved.
            int recheckThread = _interop.GetWindowThreadProcessId(hWnd, out int recheckPid);
            if (recheckThread == 0 || recheckPid == 0 || recheckPid != processId)
            {
                return new ValueTask<ForegroundCaptureResult>(
                    new ForegroundCaptureResult(null, ForegroundCaptureError.WindowVanished));
            }

            var identity = new SourceWindowIdentity(
                WindowHandle: hWnd,
                ProcessId: processId,
                ProcessName: processName,
                Title: windowTitle,
                CapturedAt: DateTimeOffset.UtcNow);

            return new ValueTask<ForegroundCaptureResult>(new ForegroundCaptureResult(identity, ForegroundCaptureError.None));
        }
        catch (UnauthorizedAccessException)
        {
            return new ValueTask<ForegroundCaptureResult>(new ForegroundCaptureResult(null, ForegroundCaptureError.AccessDenied));
        }
        catch (Exception)
        {
            return new ValueTask<ForegroundCaptureResult>(new ForegroundCaptureResult(null, ForegroundCaptureError.Unknown));
        }
    }
}
