using System;
using System.Collections.Generic;

namespace ContextBar.Core;

public sealed record FileOutputReceipt(
    string Path,
    string HashSha256);

public sealed record OperationReceipt(
    Guid OperationId,
    string ActionId,
    DateTimeOffset Timestamp,
    IReadOnlyList<FileOutputReceipt> Outputs,
    IReadOnlyList<RenameJournalEntry>? Renames = null,
    DateTimeOffset? UndoUntil = null);

public sealed record RenameJournalEntry(string OriginalPath, string CurrentPath, string HashSha256);

public sealed record UndoResult(
    bool Success,
    IReadOnlyList<string> FailedPaths);
