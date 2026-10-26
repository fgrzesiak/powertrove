using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Core;

namespace ContextBar.SampleAdapters.Providers;

/// <summary>
/// A sample context provider that simulates capturing file selections.
/// </summary>
public class SampleFileProvider : IContextProvider
{
    public string Id => "sample.file.provider";

    public ValueTask<bool> CanCaptureAsync(SourceWindowIdentity source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool canCapture = source.ProcessName?.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) == true;
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

        if (source.ProcessName?.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) != true)
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

        var files = new List<SelectedFile>
        {
            new SelectedFile(
                "C:\\Temp\\document.pdf",
                "document.pdf",
                ".pdf",
                "application/pdf",
                1024 * 500, // 500 KB
                true,
                true,
                null,
                false
            ),
            new SelectedFile(
                "C:\\Temp\\image.png",
                "image.png",
                ".png",
                "image/png",
                1024 * 1024 * 2, // 2 MB
                true,
                true,
                null,
                false
            )
        };

        var content = new FileSelection(files);

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
