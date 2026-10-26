using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions.Files;

namespace ContextBar.Core;

public sealed partial class UndoService : IFileRenameService
{
    private readonly SemaphoreSlim _renameGate = new(1, 1);
    public async Task<RenameOperation> RenameAsync(IReadOnlyList<FileRename> renames, CancellationToken cancellationToken)
    {
        await _renameGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!FileRenameRules.IsValid(renames, _fileSystem)) throw new IOException("The rename plan is no longer valid.");
            var changed = renames.Where(r => r.OriginalPath != r.TargetPath).ToArray();
            if (changed.Length == 0) throw new IOException("No names would change.");
            var journal = new List<RenameJournalEntry>();
            foreach (var r in changed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureRegularFile(r.OriginalPath);
                journal.Add(new(r.OriginalPath, r.TargetPath, await ComputeHashAsync(r.OriginalPath, cancellationToken).ConfigureAwait(false)));
            }
            Guid id = Guid.NewGuid(); DateTimeOffset expiry = _clock.GetUtcNow().AddMinutes(10);
            await MoveTransactionAsync(journal, id, expiry, cancellationToken).ConfigureAwait(false);
            expiry = _clock.GetUtcNow().AddMinutes(10);
            _history[id] = new(id, "contextbar.files.rename", _clock.GetUtcNow(), Array.Empty<FileOutputReceipt>(), journal, expiry);
            return new(id, expiry, journal.Count);
        }
        finally { _renameGate.Release(); }
    }
    private async Task<bool> CanUndoRenameAsync(OperationReceipt receipt, CancellationToken ct)
    {
        if (receipt.Renames is not { Count: > 0 } || (receipt.UndoUntil is not { } expiry || expiry <= _clock.GetUtcNow())) return false;
        var inverse = receipt.Renames.Select(r => new FileRename(r.CurrentPath, r.OriginalPath)).ToArray();
        if (!FileRenameRules.IsValid(inverse, _fileSystem)) return false;
        foreach (var r in receipt.Renames)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                EnsureRegularFile(r.CurrentPath);
                if (await ComputeHashAsync(r.CurrentPath, ct).ConfigureAwait(false) != r.HashSha256) return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        return true;
    }
    private async Task<UndoResult> UndoRenameAsync(Guid id, CancellationToken ct)
    {
        await _renameGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_history.TryGetValue(id, out var receipt) || !await CanUndoRenameAsync(receipt, ct).ConfigureAwait(false)) return new(false, Array.Empty<string>());
            var inverse = receipt.Renames!.Select(r => new RenameJournalEntry(r.CurrentPath, r.OriginalPath, r.HashSha256)).ToArray();
            try
            {
                await MoveTransactionAsync(inverse, id, receipt.UndoUntil!.Value, ct).ConfigureAwait(false);
                _history.TryRemove(id, out _); return new(true, Array.Empty<string>());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(false, Array.Empty<string>()); }
        }
        finally { _renameGate.Release(); }
    }
    private async Task MoveTransactionAsync(IReadOnlyList<RenameJournalEntry> entries, Guid id, DateTimeOffset expiry, CancellationToken ct)
    {
        var stages = entries.Select(e => new RenameStage(e, Path.Combine(Path.GetDirectoryName(e.OriginalPath)!, ".~cbren-" + Guid.NewGuid().ToString("N")))).ToArray();
        try
        {
            foreach (var s in stages)
            {
                ct.ThrowIfCancellationRequested(); EnsureRegularFile(s.Entry.OriginalPath);
                _fileSystem.MoveFile(s.Entry.OriginalPath, s.Temp); s.Current = s.Temp;
            }
            foreach (var s in stages)
            {
                ct.ThrowIfCancellationRequested(); _fileSystem.MoveFile(s.Current, s.Entry.CurrentPath); s.Current = s.Entry.CurrentPath;
            }
            // Detect changes while preparing the operation before recording success.
            foreach (var s in stages)
                if (await ComputeHashAsync(s.Current, ct).ConfigureAwait(false) != s.Entry.HashSha256) throw new IOException("An input changed during rename.");
        }
        catch (Exception original)
        {
            // Rollback must ignore cancellation and free every completed destination before
            // restoring originals. No move overwrites an existing path, including during recovery.
            foreach (var s in stages.Where(s => s.Current == s.Entry.CurrentPath))
            {
                try { _fileSystem.MoveFile(s.Current, s.Temp); s.Current = s.Temp; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            foreach (var s in stages.Where(s => s.Current != s.Entry.OriginalPath))
            {
                try { _fileSystem.MoveFile(s.Current, s.Entry.OriginalPath); s.Current = s.Entry.OriginalPath; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            var outstanding = stages.Where(s => s.Current != s.Entry.OriginalPath).Select(s => s.Entry with { CurrentPath = s.Current }).ToArray();
            if (outstanding.Length > 0)
            {
                _history[id] = new(id, "contextbar.files.rename", _clock.GetUtcNow(), Array.Empty<FileOutputReceipt>(), outstanding, expiry);
                throw new RenameRecoveryException(id, expiry, original);
            }
            throw;
        }
    }
    private static void EnsureRegularFile(string path)
    {
        // Reparse-point ancestors are rejected as well: a renamed directory junction
        // must never redirect a transaction after the user reviewed its names.
        string? current = path;
        while (current is not null)
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked paths cannot be renamed.");
            current = Path.GetDirectoryName(current);
        }
    }
    private sealed class RenameStage(RenameJournalEntry entry, string temp)
    {
        public RenameJournalEntry Entry { get; } = entry;
        public string Temp { get; } = temp;
        public string Current { get; set; } = entry.OriginalPath;
    }
}
