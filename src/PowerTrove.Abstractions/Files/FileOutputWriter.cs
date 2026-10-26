using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Abstractions.Files;

/// <summary>
/// Transactional output creation for file actions. Every output is first written
/// to a hidden temporary sibling and only moved to its final, collision-free name
/// after the engine finished successfully. A failed or canceled write therefore
/// never leaves a partial file under a real name and never replaces an existing file.
/// </summary>
public sealed class FileOutputWriter
{
    private const int MaxCommitAttempts = 32;
    private readonly IOutputFileSystem _fileSystem;
    private readonly IOutputPlanner _planner;
    private readonly string? _outputDirectoryOverride;
    private readonly List<string> _outputs = new();

    public FileOutputWriter(IOutputFileSystem fileSystem, IOutputPlanner planner, string? outputDirectoryOverride = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _outputDirectoryOverride = outputDirectoryOverride;
    }

    public IReadOnlyList<string> Outputs => _outputs;

    /// <summary>Writes an output through a stream supplied by the writer.</summary>
    public Task<string> WriteFileAsync(
        string directory,
        string baseName,
        string extension,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken,
        bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(write);
        return WriteFileFromPathAsync(directory, baseName, extension, async (tempPath, ct) =>
        {
            await using var stream = _fileSystem.CreateFile(tempPath);
            await write(stream, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }, cancellationToken, allowEmpty);
    }

    /// <summary>
    /// Writes an output through an engine that needs a file path (for example an
    /// external process). The path does not exist yet and carries the final extension.
    /// </summary>
    public async Task<string> WriteFileFromPathAsync(
        string directory,
        string baseName,
        string extension,
        Func<string, CancellationToken, Task> writeToPath,
        CancellationToken cancellationToken,
        bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(writeToPath);
        string targetDirectory = _outputDirectoryOverride ?? directory;
        EnsureDirectory(targetDirectory);

        string tempPath = MapOutputPath(Path.Combine(targetDirectory, TempName(extension)));
        try
        {
            await writeToPath(tempPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!_fileSystem.FileExists(tempPath) || (!allowEmpty && _fileSystem.GetFileLength(tempPath) == 0))
                throw new FileProcessingException(Resources.SdkStrings.Error_EngineNoOutput);

            string finalPath = Commit(tempPath, attempt => _planner.PlanFile(_outputDirectoryOverride ?? directory, baseName, extension).TargetPath, _fileSystem.MoveFile);
            _outputs.Add(finalPath);
            return finalPath;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    /// <summary>Creates a new output directory, populated in a temporary sibling first.</summary>
    public async Task<string> WriteDirectoryAsync(
        string directory,
        string name,
        Func<string, CancellationToken, Task> populate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(populate);
        string targetDirectory = _outputDirectoryOverride ?? directory;
        EnsureDirectory(targetDirectory);

        string tempPath = MapOutputPath(Path.Combine(targetDirectory, TempName(string.Empty)));
        _fileSystem.CreateDirectory(tempPath);
        try
        {
            await populate(tempPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            string finalPath = Commit(tempPath, attempt => _planner.PlanDirectory(_outputDirectoryOverride ?? directory, name).TargetPath, _fileSystem.MoveDirectory);
            _outputs.Add(finalPath);
            return finalPath;
        }
        finally
        {
            if (_fileSystem.DirectoryExists(tempPath))
            {
                try { _fileSystem.DeleteDirectory(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string Commit(string tempPath, Func<int, string> planTarget, Action<string, string> move)
    {
        IOException? last = null;
        for (int attempt = 0; attempt < MaxCommitAttempts; attempt++)
        {
            string target = planTarget(attempt);
            try
            {
                move(tempPath, target);
                return target;
            }
            catch (IOException ex)
            {
                // The planned name was taken between planning and commit; plan again.
                last = ex;
            }
        }

        throw new FileProcessingException(Resources.SdkStrings.Error_SaveNoFreeName, last!);
    }

    private void EnsureDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new FileProcessingException(Resources.SdkStrings.Error_OutputFolderUnavailable);
        if (!_fileSystem.DirectoryExists(directory))
            throw new FileProcessingException(Resources.SdkStrings.Error_OutputFolderNoLongerExists);
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (_fileSystem.FileExists(path)) _fileSystem.DeleteFile(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string MapOutputPath(string logicalPath) => _fileSystem is IOutputPathMapper mapper
        ? mapper.MapOutputPath(logicalPath) : logicalPath;

    private static string TempName(string extension) =>
        ".~cb" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() + extension;
}
