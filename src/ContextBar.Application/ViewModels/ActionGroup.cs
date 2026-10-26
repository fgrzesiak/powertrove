using System.Collections.Generic;
using ContextBar.Core.Search;

namespace ContextBar.Application.ViewModels;

public sealed record ActionGroup(string Title, IReadOnlyList<ActionSearchResult> Items);
