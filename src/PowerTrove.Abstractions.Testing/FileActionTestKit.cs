using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions.Files;

namespace PowerTrove.Abstractions.Testing;

/// <summary>A unique temporary directory that is deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "powertrove-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, byte[] content)
    {
        string path = File(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    public string Write(string name, string content) => Write(name, System.Text.Encoding.UTF8.GetBytes(content));

    /// <summary>Files created by the code under test (excluding the given inputs).</summary>
    public IReadOnlyList<string> NewFiles(params string[] inputs) =>
        Directory.GetFileSystemEntries(Path, "*", SearchOption.TopDirectoryOnly)
            .Where(p => !inputs.Contains(p, StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class Snapshots
{
    /// <summary>A fresh, available file-selection snapshot for the given paths (files or folders).</summary>
    public static ContextSnapshot ForPaths(params string[] paths)
    {
        var files = paths.Select(p =>
        {
            bool isDirectory = Directory.Exists(p);
            long? size = isDirectory || !System.IO.File.Exists(p) ? null : new FileInfo(p).Length;
            return new SelectedFile(p, Path.GetFileName(Path.TrimEndingDirectorySeparator(p)), isDirectory ? string.Empty : Path.GetExtension(p),
                null, size, IsAvailableLocally: true, IsAccessible: true, Metadata: null, IsDirectory: isDirectory);
        }).ToList();

        return new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "explorer.exe", DateTimeOffset.UtcNow),
            ContextStatus.Available,
            new FileSelection(files),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "test",
            null);
    }

    public static ContextSnapshot Stale(this ContextSnapshot snapshot) => snapshot with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };

    public static DictionaryParameters Parameters(params (string Key, string Value)[] values) =>
        new(values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Extension-based verifier for unit tests that do not exercise signature detection.
/// Production code uses a signature-based verifier.
/// </summary>
public sealed class ExtensionOnlyTypeVerifier : IFileTypeVerifier
{
    public ValueTask<FileTypeVerification> VerifyAsync(string path, FileInputFilter filter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool ok = filter.Extensions is null || filter.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        return new(ok ? FileTypeVerification.Accepted(null) : FileTypeVerification.Rejected(null, "Unexpected file type."));
    }
}
