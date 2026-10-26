using System;
using PowerTrove.Abstractions;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Core.Search;

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
