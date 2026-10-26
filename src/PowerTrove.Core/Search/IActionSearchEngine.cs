using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Core.Search;

public interface IActionSearchEngine
{
    void RefreshSignals();
    Task<IReadOnlyList<ActionSearchResult>> SearchAsync(
        string query,
        ContextSnapshot context,
        CancellationToken cancellationToken = default);
}
