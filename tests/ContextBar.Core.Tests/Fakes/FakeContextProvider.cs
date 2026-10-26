using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ContextBar.Core.Tests.Fakes;

public class FakeContextProvider : IContextProvider
{
    public string Id { get; init; } = "FakeProvider";

    public bool CanCaptureResult { get; set; } = true;

    // Configuration for capture outcome
    public ContextStatus CaptureStatus { get; set; } = ContextStatus.Available;
    public ContextContent CaptureContent { get; set; } = new NoContent();
    public string? CaptureErrorMessage { get; set; }
    public IReadOnlySet<string>? ProvidedCapabilities { get; set; }

    // Configuration for simulating delays/cancellation
    public TimeSpan CaptureDelay { get; set; } = TimeSpan.Zero;

    public ValueTask<bool> CanCaptureAsync(
        SourceWindowIdentity source,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<bool>(cancellationToken);
        }
        return new ValueTask<bool>(CanCaptureResult);
    }

    public async ValueTask<ContextSnapshot> CaptureAsync(
        Guid invocationId,
        SourceWindowIdentity source,
        CancellationToken cancellationToken)
    {
        if (CaptureDelay > TimeSpan.Zero)
        {
            await Task.Delay(CaptureDelay, cancellationToken);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var now = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        return new ContextSnapshot(
            InvocationId: invocationId,
            Source: source,
            Status: CaptureStatus,
            Content: CaptureContent,
            CapturedAt: now,
            ExpiresAt: now.AddMinutes(5),
            ProviderId: Id,
            UserMessage: CaptureErrorMessage,
            ProvidedCapabilities: ProvidedCapabilities
        );
    }
}
