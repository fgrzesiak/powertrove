using System;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Core;

public enum ForegroundCaptureError
{
    None,
    WindowVanished,
    AccessDenied,
    SelfCapturePrevented,
    Unknown
}

public sealed record ForegroundCaptureResult(
    SourceWindowIdentity? Identity,
    ForegroundCaptureError Error);

public interface IForegroundWindowCapture
{
    ValueTask<ForegroundCaptureResult> CaptureForegroundAsync(CancellationToken cancellationToken = default);
}
