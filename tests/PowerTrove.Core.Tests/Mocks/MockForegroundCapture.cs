using System;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Core;

namespace PowerTrove.Core.Tests.Mocks;

public sealed class MockForegroundCapture : IForegroundWindowCapture
{
    private ForegroundCaptureResult _resultToReturn = new(null, ForegroundCaptureError.Unknown);

    public void SetupResult(ForegroundCaptureResult result)
    {
        _resultToReturn = result;
    }

    public void SetupSuccess(SourceWindowIdentity identity)
    {
        _resultToReturn = new(identity, ForegroundCaptureError.None);
    }

    public void SetupError(ForegroundCaptureError error)
    {
        _resultToReturn = new(null, error);
    }

    public ValueTask<ForegroundCaptureResult> CaptureForegroundAsync(CancellationToken cancellationToken = default)
    {
        return new ValueTask<ForegroundCaptureResult>(_resultToReturn);
    }
}
