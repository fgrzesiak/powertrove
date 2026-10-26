using System;
using System.Collections.Generic;
using ContextBar.Abstractions;

namespace ContextBar.Core.Recipes;

public sealed class GenericActionPack : IActionPack
{
    public GenericActionPack(ActionPackInfo info, IReadOnlyList<IAction> actions)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        Actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public ActionPackInfo Info { get; }

    public IReadOnlyList<IAction> Actions { get; }
}
