using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PowerTrove.Pack.Files;

public sealed record DirectoryScanResult(
    IReadOnlyList<string> Paths,
    bool LimitReached,
    int TotalItemsFound)
{
    public int SkippedDirectories { get; init; }
}

/// <summary>
/// Scans directories for files up to a specified depth and item limit.
/// Symlinks and junction points are skipped by default to prevent cycles.
/// </summary>
public interface IDirectoryScanner
{
    DirectoryScanResult EnumerateFiles(string path, int maxDepth, int maxItems, bool followSymlinks = false);
}

public sealed class DirectoryScanner : IDirectoryScanner
{
    public DirectoryScanResult EnumerateFiles(string path, int maxDepth, int maxItems, bool followSymlinks = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));

        if (maxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Max depth cannot be negative.");

        if (maxItems < 1)
            throw new ArgumentOutOfRangeException(nameof(maxItems), "Max items must be at least 1.");

        var paths = new List<string>();
        bool limitReached = false;
        int totalItems = 0;

        try
        {
            var dirInfo = new DirectoryInfo(path);
            if (!dirInfo.Exists)
            {
                return new DirectoryScanResult(Array.Empty<string>(), false, 0);
            }

            ScanDirectory(dirInfo, 0, maxDepth, maxItems, followSymlinks, paths, ref limitReached, ref totalItems);
        }
        catch (Exception)
        {
            // If the root directory throws (e.g. access denied), return what we have (nothing)
        }

        return new DirectoryScanResult(paths, limitReached, totalItems);
    }

    private void ScanDirectory(
        DirectoryInfo dir,
        int currentDepth,
        int maxDepth,
        int maxItems,
        bool followSymlinks,
        List<string> paths,
        ref bool limitReached,
        ref int totalItems)
    {
        if (limitReached) return;

        if (!followSymlinks && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return;
        }

        try
        {
            var files = dir.EnumerateFiles();
            foreach (var file in files)
            {
                if (limitReached) break;

                if (!followSymlinks && file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                totalItems++;
                if (paths.Count < maxItems)
                {
                    paths.Add(file.FullName);
                }
                else
                {
                    limitReached = true;
                    break;
                }
            }

            if (limitReached || currentDepth >= maxDepth)
            {
                return;
            }

            var subDirs = dir.EnumerateDirectories();
            foreach (var subDir in subDirs)
            {
                if (limitReached) break;
                ScanDirectory(subDir, currentDepth + 1, maxDepth, maxItems, followSymlinks, paths, ref limitReached, ref totalItems);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Silently skip directories we cannot access
        }
        catch (PathTooLongException)
        {
            // Skip paths that are too long
        }
        catch (DirectoryNotFoundException)
        {
            // Directory was deleted while scanning
        }
    }
}
