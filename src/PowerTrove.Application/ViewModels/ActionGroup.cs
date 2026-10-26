using System.Collections.Generic;
using PowerTrove.Core.Search;

namespace PowerTrove.Application.ViewModels;

public sealed record ActionGroup(string Title, IReadOnlyList<ActionSearchResult> Items);
