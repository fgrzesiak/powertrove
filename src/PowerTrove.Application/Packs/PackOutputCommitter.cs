using PowerTrove.Abstractions.Files;
using PowerTrove.Core.Extensibility.PackProtocol;

namespace PowerTrove.Application.Packs;

/// <summary>Only the host publishes outputs. Worker destination claims are checked against the request.</summary>
public sealed class PackOutputCommitter
{
    public async Task<ActionReceipt> CommitAsync(string staging, FileSelectionPayload selection, ResultMessage result,
        CancellationToken cancellationToken, string? tableOutputDirectory = null)
    {
        var written = new List<string>();
        var failures = result.Receipt.Failures.ToList();
        try
        {
            // Validate the entire inventory before creating the first real output.
            var entries = Validate(staging, selection, result.Outputs, tableOutputDirectory);
            var files = new PhysicalOutputFileSystem();
            var writer = new FileOutputWriter(files, new OutputPlanner(files));
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Directory)
                    written.Add(await writer.WriteDirectoryAsync(entry.Output.IntendedDirectory, entry.Output.IntendedName,
                        (target, token) => CopyDirectoryAsync(entry.Source, target, token), cancellationToken).ConfigureAwait(false));
                else
                    written.Add(await writer.WriteFileAsync(entry.Output.IntendedDirectory,
                        Path.GetFileNameWithoutExtension(entry.Output.IntendedName), Path.GetExtension(entry.Output.IntendedName),
                        async (target, token) =>
                        {
                            RejectReparse(entry.Source);
                            await using var input = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                            await input.CopyToAsync(target, token).ConfigureAwait(false);
                        }, cancellationToken, allowEmpty: true).ConfigureAwait(false));
            }
            return result.Receipt with { OutputPaths = written, IsUndoable = false, UndoOperationId = null, UndoUntil = null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            failures.Add(new ActionFailure(null, PackStrings.OutputFailed));
            return result.Receipt with { IsSuccess = false, IsCanceled = result.Receipt.IsCanceled || ex is OperationCanceledException,
                OutputPaths = written, Failures = failures, IsUndoable = false, UndoOperationId = null, UndoUntil = null,
                Message = PackStrings.OutputFailed };
        }
    }

    private static IReadOnlyList<Entry> Validate(string staging, FileSelectionPayload selection, IReadOnlyList<StagedOutput> outputs,
        string? tableOutputDirectory)
    {
        string root = Canonical(staging);
        RejectReparse(root);
        var allowed = selection.Files.Select(f => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Canonical(f.Path)))!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A table request authorizes exactly the output directory the host chose for it.
        if (tableOutputDirectory is not null) allowed.Add(Canonical(tableOutputDirectory));
        var entries = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in outputs)
        {
            string source = Path.GetFullPath(Path.Combine(root, output.Path.Replace('/', Path.DirectorySeparatorChar)));
            string destination = Canonical(output.IntendedDirectory);
            if (!source.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(source) || !allowed.Contains(destination) || !Directory.Exists(destination)
                || output.IntendedName != Path.GetFileName(output.IntendedName) || output.IntendedName is "" or "." or ".."
                || output.IntendedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException();
            RejectReparse(source); RejectReparse(destination);
            bool directory = Directory.Exists(source);
            if (!directory && !File.Exists(source)) throw new InvalidDataException();
            if (directory) _ = Tree(source).ToArray();
            entries.Add(new Entry(output, source, directory));
        }
        // Overlapping entries must not copy the same output twice.
        if (entries.Any(a => entries.Any(b => a != b && b.Source.StartsWith(a.Source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException();
        return entries;
    }

    private static async Task CopyDirectoryAsync(string source, string target, CancellationToken token)
    {
        foreach (var path in Tree(source))
        {
            token.ThrowIfCancellationRequested();
            string destination = Path.Combine(target, Path.GetRelativePath(source, path));
            RejectReparse(path); RejectReparse(destination);
            if (Directory.Exists(path)) Directory.CreateDirectory(destination);
            else
            {
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }
        }
    }
    private static IEnumerable<string> Tree(string root)
    {
        var pending = new Stack<string>(); pending.Push(root); int count = 0;
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (++count > 10000) throw new InvalidDataException();
                RejectReparse(path); yield return path;
                if (Directory.Exists(path)) pending.Push(path);
            }
    }
    internal static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length > 4096 || path.Any(char.IsControl))
            throw new InvalidDataException();
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(path), full, StringComparison.OrdinalIgnoreCase)
            || OperatingSystem.IsWindows() && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.AsSpan(2).Contains(':')
                || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)) throw new InvalidDataException();
        return full;
    }
    internal static void RejectReparse(string path)
    {
        for (string? parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.Exists(parent) || Directory.Exists(parent)) && (File.GetAttributes(parent) &
                (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0)
                throw new UnauthorizedAccessException();
    }
    private sealed record Entry(StagedOutput Output, string Source, bool Directory);
}
