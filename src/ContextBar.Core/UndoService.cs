using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions.Files;

namespace ContextBar.Core;

public sealed partial class UndoService : IUndoService
{
    private readonly IOutputFileSystem _fileSystem;
    private readonly ConcurrentDictionary<Guid, OperationReceipt> _history = new();

    private readonly TimeProvider _clock;

    public UndoService(IOutputFileSystem fileSystem, TimeProvider? clock = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<OperationReceipt> RecordOperationAsync(
        Guid operationId,
        string actionId,
        IReadOnlyList<string> outputPaths,
        CancellationToken cancellationToken)
    {
        var outputs = new List<FileOutputReceipt>();

        foreach (var path in outputPaths)
        {
            if (!_fileSystem.FileExists(path))
            {
                continue; // Skip if file doesn't exist, though this shouldn't happen for successful operations
            }

            string hash = await ComputeHashAsync(path, cancellationToken).ConfigureAwait(false);
            outputs.Add(new FileOutputReceipt(path, hash));
        }

        var receipt = new OperationReceipt(operationId, actionId, DateTimeOffset.UtcNow, outputs);
        _history[operationId] = receipt;

        return receipt;
    }

    public IReadOnlyList<OperationReceipt> GetHistory()
    {
        return _history.Values.OrderByDescending(r => r.Timestamp).ToList();
    }

    public async Task<bool> CanUndoAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (!_history.TryGetValue(operationId, out var receipt))
        {
            return false;
        }

        if (receipt.Renames is not null)
            return await CanUndoRenameAsync(receipt, cancellationToken).ConfigureAwait(false);

        if (receipt.Outputs.Count == 0)
        {
            return false;
        }

        foreach (var output in receipt.Outputs)
        {
            if (!_fileSystem.FileExists(output.Path))
            {
                return false;
            }

            string currentHash = await ComputeHashAsync(output.Path, cancellationToken).ConfigureAwait(false);
            if (currentHash != output.HashSha256)
            {
                return false;
            }
        }

        return true;
    }

    public async Task<UndoResult> UndoAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (_history.TryGetValue(operationId, out var renameReceipt) && renameReceipt.Renames is not null)
            return await UndoRenameAsync(operationId, cancellationToken).ConfigureAwait(false);

        if (!await CanUndoAsync(operationId, cancellationToken).ConfigureAwait(false))
        {
            return new UndoResult(false, Array.Empty<string>());
        }

        if (!_history.TryGetValue(operationId, out var receipt))
        {
            return new UndoResult(false, Array.Empty<string>());
        }

        var failedPaths = new List<string>();

        foreach (var output in receipt.Outputs)
        {
            try
            {
                _fileSystem.DeleteFile(output.Path);
            }
            catch
            {
                failedPaths.Add(output.Path);
            }
        }

        if (failedPaths.Count == 0)
        {
            _history.TryRemove(operationId, out _);
            return new UndoResult(true, Array.Empty<string>());
        }

        return new UndoResult(false, failedPaths);
    }

    private async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = _fileSystem.OpenRead(path);
        using var sha256 = SHA256.Create();
#if NET6_0_OR_GREATER
        byte[] hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
#else
        // Fallback for older .NET if needed, though project targets modern .NET
        byte[] hashBytes = sha256.ComputeHash(stream);
#endif
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
