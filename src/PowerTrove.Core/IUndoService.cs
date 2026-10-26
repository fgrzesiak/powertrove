using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Core;

public interface IUndoService
{
    Task<OperationReceipt> RecordOperationAsync(
        Guid operationId,
        string actionId,
        IReadOnlyList<string> outputPaths,
        CancellationToken cancellationToken);

    IReadOnlyList<OperationReceipt> GetHistory();

    Task<bool> CanUndoAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    Task<UndoResult> UndoAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}
