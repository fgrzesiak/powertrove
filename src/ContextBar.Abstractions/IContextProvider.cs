using System;
using System.Threading;
using System.Threading.Tasks;

namespace ContextBar.Abstractions;

public interface IContextProvider
{
    string Id { get; }
    ValueTask<bool> CanCaptureAsync(
        SourceWindowIdentity source,
        CancellationToken cancellationToken);
    ValueTask<ContextSnapshot> CaptureAsync(
        Guid invocationId,
        SourceWindowIdentity source,
        CancellationToken cancellationToken);
}
