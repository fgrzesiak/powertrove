using System;

namespace PowerTrove.Abstractions.Files;

public sealed record OutputPlan(
    string OriginalPath,
    string TargetPath,
    bool RequiresFolderCreation,
    bool WillOverwrite);
