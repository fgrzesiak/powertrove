using System;
using ContextBar.Abstractions;
using ContextBar.Core.Catalog;

namespace ContextBar.Core.Search;

public sealed record ActionSearchResult(
    IAction Action,
    bool IsCompatible,
    string? IncompatibilityReason,
    int Rank,
    ActionHighlight Highlight = ActionHighlight.None,
    SelectionFit Fit = SelectionFit.None,
    string? AvailabilityReason = null,
    int TextMatchScore = 0,
    PackInstallState InstallState = PackInstallState.Installed);
