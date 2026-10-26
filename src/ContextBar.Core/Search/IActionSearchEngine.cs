using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ContextBar.Core.Search;

public interface IActionSearchEngine
{
    void RefreshSignals();
    Task<IReadOnlyList<ActionSearchResult>> SearchAsync(
        string query,
        ContextSnapshot context,
        CancellationToken cancellationToken = default);
}
