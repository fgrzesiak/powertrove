using System;
using System.IO;

namespace ContextBar.Abstractions.Files;

/// <summary>
/// File system operations available to actions. No member overwrites or
/// truncates an existing file: creation and moves fail if the target exists.
/// </summary>
public interface IOutputFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void DeleteFile(string path);
    Stream OpenRead(string path);
    Stream CreateFile(string path);
    long GetFileLength(string path);

    /// <summary>Moves a file; throws <see cref="IOException"/> if the destination exists.</summary>
    void MoveFile(string sourcePath, string destinationPath);

    /// <summary>Moves a directory; throws <see cref="IOException"/> if the destination exists.</summary>
    void MoveDirectory(string sourcePath, string destinationPath);

    /// <summary>Deletes a directory created by the action itself (recursive).</summary>
    void DeleteDirectory(string path);
}

/// <summary>Optional physical output mapping for isolated engines that require a path instead of a stream.</summary>
public interface IOutputPathMapper
{
    string MapOutputPath(string logicalPath);
}

public sealed class PhysicalOutputFileSystem : IOutputFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteFile(string path) => File.Delete(path);
    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
    // Never truncate an existing file, including one created after output planning.
    // O_EXCL/CREATE_NEW style semantics close the plan-to-write race.
    public Stream CreateFile(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
    public long GetFileLength(string path) => new FileInfo(path).Length;
    public void MoveFile(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath, overwrite: false);

    public void MoveDirectory(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new IOException("The destination already exists.");
        Directory.Move(sourcePath, destinationPath);
    }

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
}
