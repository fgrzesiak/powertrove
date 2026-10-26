using System;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Core;

namespace PowerTrove.SampleAdapters.Providers;

/// <summary>
/// A sample context provider that simulates capturing text from a mock application.
/// </summary>
public class SampleTextProvider : IContextProvider
{
    public string Id => "sample.text.provider";

    public ValueTask<bool> CanCaptureAsync(SourceWindowIdentity source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // For sample purposes, pretend we only capture from "notepad.exe"
        bool canCapture = source.ProcessName?.Equals("notepad.exe", StringComparison.OrdinalIgnoreCase) == true;
        return new ValueTask<bool>(canCapture);
    }

    public ValueTask<ContextSnapshot> CaptureAsync(Guid invocationId, SourceWindowIdentity source, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ValueTask<ContextSnapshot>(new ContextSnapshot(
                invocationId,
                source,
                ContextStatus.Canceled,
                new NoContent(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Id,
                "Canceled."
            ));
        }

        if (source.ProcessName?.Equals("notepad.exe", StringComparison.OrdinalIgnoreCase) != true)
        {
            return new ValueTask<ContextSnapshot>(new ContextSnapshot(
                invocationId,
                source,
                ContextStatus.Unsupported,
                new NoContent(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Id,
                "Unsupported application."
            ));
        }

        var content = new TextSelection("This is some sample text captured from Notepad.");

        var snapshot = new ContextSnapshot(
            invocationId,
            source,
            ContextStatus.Available,
            content,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            Id,
            null
        );

        return new ValueTask<ContextSnapshot>(snapshot);
    }
}
