using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using ContextBar.Core.Extensibility.PackProtocol;

namespace ContextBar.PackHost;

/// <summary>
/// Mediates SDK file operations, not an OS sandbox for trusted pack code. Physical engine paths are
/// redirected by FileOutputWriter through IOutputPathMapper before invoking any path-based engine.
/// </summary>
public sealed class StagingOutputFileSystem : IOutputFileSystem, IOutputPathMapper
{
    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly FileSelectionPayload _selection;
    private readonly bool _readOnly;
    private readonly ConcurrentDictionary<string, string> _mapped = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _mappingGate = new();
    private readonly PhysicalOutputFileSystem _physical = new();

    public StagingOutputFileSystem(FileSelectionPayload selection, string stagingDirectory, bool readOnly = false)
    {
        _selection = selection;
        _root = Canonical(stagingDirectory);
        _rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _readOnly = readOnly;
        RejectReparseAncestors(_root);
        if (!readOnly && (!Directory.Exists(_root) || Directory.EnumerateFileSystemEntries(_root).Any()))
            throw new UnauthorizedAccessException("Staging directory must be empty and owned by the host.");
    }

    public string MapOutputPath(string logicalPath)
    {
        lock (_mappingGate) return MapPath(logicalPath);
    }

    private string MapPath(string logicalPath)
    {
        if (_readOnly) throw new UnauthorizedAccessException("Preview cannot create outputs.");
        string path = Canonical(logicalPath);
        if (IsStaged(path)) { RejectReparseAncestors(path); return path; }
        if (_mapped.TryGetValue(path, out string? existing)) return existing;
        string directory = Path.GetDirectoryName(path) ?? throw new UnauthorizedAccessException();
        if (!_directories.TryGetValue(directory, out string? bucket))
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))[..16].ToLowerInvariant();
            bucket = Path.Combine(_root, hash);
            if (_directories.Values.Contains(bucket, StringComparer.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Staging mapping collision.");
            Directory.CreateDirectory(bucket);
            _directories[directory] = bucket;
        }
        string mapped = Path.Combine(bucket, Path.GetFileName(path));
        RejectReparseAncestors(mapped);
        _mapped[path] = mapped;
        return mapped;
    }

    public IReadOnlyList<StagedOutput> DescribeOutputs(IReadOnlyList<string> logicalOutputs)
    {
        var result = new List<StagedOutput>();
        foreach (string output in logicalOutputs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string logical = Canonical(output);
            if (!_mapped.TryGetValue(logical, out string? staged) || (!File.Exists(staged) && !Directory.Exists(staged)))
                throw new UnauthorizedAccessException("Receipt contains an unregistered output.");
            RejectReparseTree(staged);
            result.Add(new StagedOutput(Path.GetRelativePath(_root, staged).Replace(Path.DirectorySeparatorChar, '/'),
                Path.GetDirectoryName(logical)!, Path.GetFileName(logical)));
        }
        return result.AsReadOnly();
    }

    public bool FileExists(string path)
    {
        string canonical = Canonical(path);
        if (IsStaged(canonical)) { RejectReparseAncestors(canonical); return File.Exists(canonical); }
        if (_mapped.TryGetValue(canonical, out string? staged) && File.Exists(staged)) return true;
        RejectReparseAncestors(canonical);
        return File.Exists(canonical); // Metadata only: the planner checks real collision candidates.
    }
    public bool DirectoryExists(string path)
    {
        string canonical = Canonical(path);
        RejectReparseAncestors(canonical);
        return _mapped.TryGetValue(canonical, out string? staged) && Directory.Exists(staged) || Directory.Exists(canonical);
    }
    public Stream OpenRead(string path)
    {
        string canonical = Canonical(path);
        if (_mapped.TryGetValue(canonical, out string? staged) && File.Exists(staged)) canonical = staged;
        RequireRead(canonical);
        return _physical.OpenRead(canonical);
    }
    public long GetFileLength(string path)
    {
        string canonical = Canonical(path);
        if (_mapped.TryGetValue(canonical, out string? staged) && File.Exists(staged)) canonical = staged;
        RequireRead(canonical);
        return _physical.GetFileLength(canonical);
    }
    public Stream CreateFile(string path) => _physical.CreateFile(MapOutputPath(path));
    public void CreateDirectory(string path) => _physical.CreateDirectory(MapOutputPath(path));
    public void DeleteFile(string path) => _physical.DeleteFile(RequireStaged(path));
    public void DeleteDirectory(string path) => _physical.DeleteDirectory(RequireStaged(path));
    public void MoveFile(string sourcePath, string destinationPath) => _physical.MoveFile(RequireStaged(sourcePath), MapOutputPath(destinationPath));
    public void MoveDirectory(string sourcePath, string destinationPath) => _physical.MoveDirectory(RequireStaged(sourcePath), MapOutputPath(destinationPath));

    private string RequireStaged(string path)
    {
        if (_readOnly) throw new UnauthorizedAccessException("Preview cannot change files.");
        string canonical = Canonical(path);
        if (_mapped.TryGetValue(canonical, out string? staged)) canonical = staged;
        if (!IsStaged(canonical) || canonical == _root) throw new UnauthorizedAccessException("Only staging outputs can be changed.");
        RejectReparseAncestors(canonical);
        return canonical;
    }
    private void RequireRead(string path)
    {
        RejectReparseAncestors(path);
        if (IsStaged(path)) return;
        foreach (var selected in _selection.Files)
        {
            string input = Canonical(selected.Path);
            if (path.Equals(input, StringComparison.OrdinalIgnoreCase) || selected.IsDirectory
                && path.StartsWith(input.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new UnauthorizedAccessException("Read is outside the explicit selection.");
    }
    private bool IsStaged(string path) => path.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase);
    internal static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length > 4096 || path.Any(char.IsControl))
            throw new UnauthorizedAccessException("Invalid local path.");
        string full = Path.GetFullPath(path);
        if (!string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), full.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || (OperatingSystem.IsWindows() && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.AsSpan(2).Contains(':'))))
            throw new UnauthorizedAccessException("Noncanonical local path.");
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new UnauthorizedAccessException("Remote paths are not supported.");
        return full;
    }
    internal static void RejectReparseAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            const FileAttributes rejected = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;
            if ((File.GetAttributes(current) & rejected) != 0) throw new UnauthorizedAccessException("Reparse or unavailable paths are not supported.");
        }
    }
    internal static void RejectReparseTree(string path)
    {
        RejectReparseAncestors(path);
        if (!Directory.Exists(path)) return;
        // Walk without following any directory reparse point.
        var pending = new Stack<string>(); pending.Push(path); int count = 0;
        while (pending.TryPop(out var directory))
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 10000) throw new UnauthorizedAccessException("Too many staging entries.");
                RejectReparseAncestors(child);
                if (Directory.Exists(child)) pending.Push(child);
            }
    }
}
