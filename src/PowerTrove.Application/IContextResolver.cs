using System;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Core;

namespace PowerTrove.Application;

public interface IContextResolver
{
    ValueTask<ContextSnapshot> ResolveContextAsync(
        Guid invocationId,
        SourceWindowIdentity source,
        CancellationToken cancellationToken);
}
