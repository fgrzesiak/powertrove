using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ContextBar.Abstractions.Files;

public sealed record FileRename(string OriginalPath, string TargetPath);
public sealed record RenameOperation(Guid OperationId, DateTimeOffset UndoUntil, int RenamedCount);
public interface IFileRenameService
{
    Task<RenameOperation> RenameAsync(IReadOnlyList<FileRename> renames, CancellationToken cancellationToken);
}
public sealed class RenameRecoveryException : IOException
{
    public RenameRecoveryException(Guid operationId, DateTimeOffset undoUntil, Exception inner) : base("The rename could not be fully rolled back.", inner)
    { OperationId = operationId; UndoUntil = undoUntil; }
    public Guid OperationId { get; }
    public DateTimeOffset UndoUntil { get; }
}

/// <summary>Windows name rules apply on every host. Validation never returns paths in messages.</summary>
public static class FileRenameRules
{
    public static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c))) return false;
        string device = name.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        return device is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") &&
            !(device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(device[3]));
    }
    public static bool IsValid(IReadOnlyList<FileRename> renames, IOutputFileSystem fs)
    {
        if (renames.Count is < 1 or > 1000) return false;
        var originals = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in renames)
        {
            if (!Path.IsPathFullyQualified(r.OriginalPath) || !Path.IsPathFullyQualified(r.TargetPath) ||
                Path.GetFullPath(r.OriginalPath) != r.OriginalPath || Path.GetFullPath(r.TargetPath) != r.TargetPath ||
                !string.Equals(Path.GetDirectoryName(r.OriginalPath), Path.GetDirectoryName(r.TargetPath), StringComparison.OrdinalIgnoreCase) ||
                !IsValidName(Path.GetFileName(r.TargetPath)) || r.TargetPath.Length > 32760 ||
                !originals.Add(r.OriginalPath) || !targets.Add(r.TargetPath) || !fs.FileExists(r.OriginalPath) || fs.DirectoryExists(r.OriginalPath)) return false;
        }
        return renames.All(r => !fs.DirectoryExists(r.TargetPath) && (!fs.FileExists(r.TargetPath) || originals.Contains(r.TargetPath)));
    }
}
