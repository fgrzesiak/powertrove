using System;
using System.Collections.Generic;
using System.IO;

namespace PowerTrove.Abstractions.Files;

public sealed class OutputPlanner : IOutputPlanner
{
    private const int MaxCollisionAttempts = 10_000;
    private readonly IOutputFileSystem _fileSystem;

    public OutputPlanner(IOutputFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public OutputPlan PlanOutput(string sourcePath, string targetDirectory, string targetExtension, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Source path cannot be null or empty.", nameof(sourcePath));
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("Target directory cannot be null or empty.", nameof(targetDirectory));
        if (string.IsNullOrWhiteSpace(targetExtension))
            throw new ArgumentException("Target extension cannot be null or empty.", nameof(targetExtension));

        if (!targetExtension.StartsWith("."))
        {
            targetExtension = "." + targetExtension;
        }

        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);

        if (!overwrite)
        {
            var plan = PlanUnique(targetDirectory, counter => counter == 0
                ? fileNameWithoutExtension + targetExtension
                : $"{fileNameWithoutExtension} ({counter}){targetExtension}", reserved: null);
            return plan with { OriginalPath = sourcePath };
        }

        string targetPath = Path.Combine(targetDirectory, fileNameWithoutExtension + targetExtension);
        bool requiresFolderCreation = !_fileSystem.DirectoryExists(targetDirectory);
        return new OutputPlan(sourcePath, targetPath, requiresFolderCreation, _fileSystem.FileExists(targetPath));
    }

    public OutputPlan PlanFile(string targetDirectory, string baseName, string extension, ISet<string>? reserved = null)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("Target directory cannot be null or empty.", nameof(targetDirectory));
        if (string.IsNullOrWhiteSpace(baseName))
            throw new ArgumentException("Base name cannot be null or empty.", nameof(baseName));

        extension ??= string.Empty;
        if (extension.Length > 0 && !extension.StartsWith("."))
            extension = "." + extension;

        string safeBase = OutputNames.Sanitize(baseName);
        return PlanUnique(targetDirectory, counter => counter == 0
            ? safeBase + extension
            : $"{safeBase} ({counter}){extension}", reserved);
    }

    public OutputPlan PlanDirectory(string targetDirectory, string name, ISet<string>? reserved = null)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("Target directory cannot be null or empty.", nameof(targetDirectory));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Directory name cannot be null or empty.", nameof(name));

        string safeName = OutputNames.Sanitize(name);
        return PlanUnique(targetDirectory, counter => counter == 0 ? safeName : $"{safeName} ({counter})", reserved);
    }

    private OutputPlan PlanUnique(string targetDirectory, Func<int, string> nameForAttempt, ISet<string>? reserved)
    {
        for (int counter = 0; counter < MaxCollisionAttempts; counter++)
        {
            string candidate = Path.Combine(targetDirectory, nameForAttempt(counter));
            bool taken = _fileSystem.FileExists(candidate)
                || _fileSystem.DirectoryExists(candidate)
                || (reserved != null && reserved.Contains(candidate));
            if (!taken)
            {
                return new OutputPlan(candidate, candidate, !_fileSystem.DirectoryExists(targetDirectory), false);
            }
        }

        throw new IOException("Could not find a free output name.");
    }

    public void PrepareOutputFolder(OutputPlan plan)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));

        if (plan.RequiresFolderCreation)
        {
            string? targetDir = Path.GetDirectoryName(plan.TargetPath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                _fileSystem.CreateDirectory(targetDir);
            }
        }
    }
}

public static class OutputNames
{
    private static readonly HashSet<char> InvalidChars = new(Path.GetInvalidFileNameChars())
    {
        '<', '>', ':', '"', '/', '\\', '|', '?', '*'
    };

    /// <summary>Replaces characters that are invalid in Windows file names and trims trailing dots/spaces.</summary>
    public static string Sanitize(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (InvalidChars.Contains(chars[i]) || char.IsControl(chars[i])) chars[i] = '_';
        }

        string result = new string(chars).Trim().TrimEnd('.', ' ');
        return result.Length == 0 ? "output" : result;
    }
}
