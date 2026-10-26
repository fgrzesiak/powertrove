using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Core;

namespace PowerTrove.Application;

public class ContextResolver : IContextResolver
{
    private readonly IEnumerable<IContextProvider> _providers;

    public ContextResolver(IEnumerable<IContextProvider> providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public async ValueTask<ContextSnapshot> ResolveContextAsync(
        Guid invocationId,
        SourceWindowIdentity source,
        CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            try
            {
                if (await provider.CanCaptureAsync(source, cancellationToken).ConfigureAwait(false))
                {
                    return await provider.CaptureAsync(invocationId, source, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // Rethrow cancellation
            }
            catch (Exception ex)
            {
                return new ContextSnapshot(
                    invocationId,
                    source,
                    ContextStatus.Failed,
                    new NoContent(),
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddMinutes(5),
                    provider.Id,
                    $"Provider failed: {ex.Message}");
            }
        }

        return new ContextSnapshot(
            invocationId,
            source,
            ContextStatus.Unsupported,
            new NoContent(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "none",
            "No selection available.");
    }
}
