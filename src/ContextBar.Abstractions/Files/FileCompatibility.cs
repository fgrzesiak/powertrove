using System;
using System.Collections.Generic;
using System.Linq;

namespace ContextBar.Abstractions.Files;

public enum MediaCategory
{
    Unknown,
    Image,
    Audio,
    Video,
    Document,
    Directory,
    Pdf,
    Archive
}

/// <summary>
/// Extension-based category detection for display and coarse matching only.
/// Actions verify real content with <see cref="IFileTypeVerifier"/> before processing.
/// </summary>

public static class FileCompatibility
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".avif", ".heic", ".heif" };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".aac", ".ogg", ".flac", ".m4a" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm" };
    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf" };
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip" };
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".csv" };

    public static MediaCategory DetectCategory(string extension, bool isDirectory)
    {
        if (isDirectory) return MediaCategory.Directory;
        if (string.IsNullOrEmpty(extension)) return MediaCategory.Unknown;

        if (ImageExtensions.Contains(extension)) return MediaCategory.Image;
        if (AudioExtensions.Contains(extension)) return MediaCategory.Audio;
        if (VideoExtensions.Contains(extension)) return MediaCategory.Video;
        if (PdfExtensions.Contains(extension)) return MediaCategory.Pdf;
        if (ArchiveExtensions.Contains(extension)) return MediaCategory.Archive;
        if (DocumentExtensions.Contains(extension)) return MediaCategory.Document;

        return MediaCategory.Unknown;
    }

    public static ActionAvailability CheckCompatibility(
        FileSelection selection,
        MediaCategory expectedCategory,
        bool requireAllAccessible = true,
        bool supportsRecursion = false)
    {
        if (selection == null)
        {
            throw new ArgumentNullException(nameof(selection));
        }

        if (selection.Files.Count == 0)
        {
            return new ActionAvailability(false, "No files selected.");
        }

        var validFiles = GetSupportedFiles(selection, expectedCategory, requireAllAccessible, supportsRecursion);

        if (validFiles.Count == 0)
        {
            return new ActionAvailability(false, $"No supported {expectedCategory} files in selection.");
        }
        else if (validFiles.Count < selection.Files.Count)
        {
            return new ActionAvailability(true, $"Mixed selection: {selection.Files.Count - validFiles.Count} file(s) will be skipped.");
        }

        return new ActionAvailability(true);
    }

    public static IReadOnlyList<SelectedFile> GetSupportedFiles(
        FileSelection selection,
        MediaCategory expectedCategory,
        bool requireAllAccessible = true,
        bool supportsRecursion = false)
    {
        return selection.Files.Where(f =>
            f.IsAvailableLocally &&
            (!requireAllAccessible || f.IsAccessible) &&
            ((f.IsDirectory && supportsRecursion) || (!f.IsDirectory && DetectCategory(f.Extension, f.IsDirectory) == expectedCategory))
        ).ToList();
    }
}
