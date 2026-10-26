using System.Collections.Generic;
using System.Threading;

namespace ContextBar.Core;

public interface IActionRegistry
{
    void Register(IAction action);
    IAction? GetAction(string id);
    IEnumerable<IAction> GetAllActions();
    IAsyncEnumerable<IAction> GetAvailableActionsAsync(ContextSnapshot context, CancellationToken cancellationToken = default);
}
