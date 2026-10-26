using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Core;

namespace PowerTrove.Platform.Windows.Capture;

public sealed class ExplorerContextProvider : IContextProvider
{
    private readonly IShellInterop _shellInterop;

    // A blocked COM server holds this slot until it returns.
    private static readonly SemaphoreSlim CaptureSlot = new(1, 1);
    private readonly TimeSpan _captureTimeout = TimeSpan.FromSeconds(2);

    public ExplorerContextProvider(IShellInterop shellInterop, TimeSpan captureTimeout)
    {
        _shellInterop = shellInterop ?? throw new ArgumentNullException(nameof(shellInterop));
        if (captureTimeout <= TimeSpan.Zero || captureTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(captureTimeout));
        _captureTimeout = captureTimeout;
    }

    public ExplorerContextProvider(IShellInterop shellInterop) : this(shellInterop, TimeSpan.FromSeconds(2))
    {
    }

    public ExplorerContextProvider() : this(new WindowsShellInterop(), TimeSpan.FromSeconds(2))
    {
    }

    public string Id => "powertrove.provider.windows.explorer";

    public ValueTask<bool> CanCaptureAsync(SourceWindowIdentity source, CancellationToken cancellationToken)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        bool isExplorer = string.Equals(source.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        return new ValueTask<bool>(isExplorer);
    }

    public async ValueTask<ContextSnapshot> CaptureAsync(Guid invocationId, SourceWindowIdentity source, CancellationToken cancellationToken)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = capturedAt.AddMinutes(5); // Arbitrary expiry
        var capabilities = new HashSet<string> { "shell.selection.read.v1" };

        ContextSnapshot CreateSnapshot(ContextStatus status, ContextContent content, string? userMessage = null)
        {
            return new ContextSnapshot(
                InvocationId: invocationId,
                Source: source,
                Status: status,
                Content: content,
                CapturedAt: capturedAt,
                ExpiresAt: expiresAt,
                ProviderId: Id,
                UserMessage: userMessage,
                ProvidedCapabilities: capabilities
            );
        }

        if (cancellationToken.IsCancellationRequested)
            return CreateSnapshot(ContextStatus.Canceled, new NoContent());

        if (!CaptureSlot.Wait(0))
            return CreateSnapshot(ContextStatus.TimedOut, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExplorerCaptureBusy);

        using var deadline = new CancellationTokenSource(_captureTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        var completion = new TaskCompletionSource<ContextSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var worker = new Thread(() =>
            {
                ContextSnapshot result;
                try
                {
                    if (_shellInterop.TryGetSelectedFiles(source.WindowHandle, out var files, out _))
                    {
                        if (files.Count > 0)
                        {
                            result = CreateSnapshot(ContextStatus.Available, new FileSelection(files));
                        }
                        else
                        {
                            result = CreateSnapshot(ContextStatus.Empty, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExplorerNoSelection);
                        }
                    }
                    else
                    {
                        result = CreateSnapshot(ContextStatus.Unsupported, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExplorerUnsupportedView);
                    }
                }
                catch (Exception)
                {
                    result = CreateSnapshot(ContextStatus.Failed, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExplorerError);
                }
                finally
                {
                    CaptureSlot.Release();
                }
                completion.TrySetResult(result);
            })
            { IsBackground = true, Name = "PowerTrove Explorer capture" };

            if (OperatingSystem.IsWindows()) worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
        catch (Exception)
        {
            CaptureSlot.Release();
            return CreateSnapshot(ContextStatus.Failed, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExplorerCaptureStartFailed);
        }

        try
        {
            var result = await completion.Task.WaitAsync(token).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                return CreateSnapshot(ContextStatus.Canceled, new NoContent());
            if (deadline.IsCancellationRequested)
                return CreateSnapshot(ContextStatus.TimedOut, new NoContent(), PowerTrove.Platform.Windows.Resources.Strings.ExplorerCaptureTimedOut);
            return result;
        }
        catch (OperationCanceledException)
        {
            return CreateSnapshot(
                cancellationToken.IsCancellationRequested ? ContextStatus.Canceled : ContextStatus.TimedOut,
                new NoContent(),
                cancellationToken.IsCancellationRequested ? null : PowerTrove.Platform.Windows.Resources.Strings.ExplorerCaptureTimedOut);
        }
    }
}
