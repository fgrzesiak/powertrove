using System;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Core;

namespace ContextBar.Application;

public interface IContextResolver
{
    ValueTask<ContextSnapshot> ResolveContextAsync(
        Guid invocationId,
        SourceWindowIdentity source,
        CancellationToken cancellationToken);
}
