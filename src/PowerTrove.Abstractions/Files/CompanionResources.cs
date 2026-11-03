using System;
using System.IO;
using System.Linq;

namespace PowerTrove.Abstractions.Files;

/// <summary>
/// The one rule for files a selected document references, such as the images of a Markdown file: a
/// relative reference that stays inside the document's own folder or one of its subfolders. Absolute,
/// rooted, drive, UNC, alternate-stream and parent-escaping references are refused. Hosts additionally
/// refuse reparse points, offline files and network drives, and grant reads only for selected documents.
/// </summary>
public static class CompanionResources
{
    public const int MaximumReferenceLength = 1024;
    /// <summary>Bytes per companion file; callers may apply a lower limit.</summary>
    public const long MaximumFileBytes = 25L * 1024 * 1024;

    /// <param name="reference">The decoded relative path as written in the document, with / or \ separators.</param>
    public static bool TryResolve(string documentPath, string reference, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(documentPath) || !Path.IsPathFullyQualified(documentPath)
            || string.IsNullOrWhiteSpace(reference) || reference.Length > MaximumReferenceLength
            || reference.Any(c => char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')
            || reference.StartsWith('/') || reference.StartsWith('\\') || Path.IsPathRooted(reference))
            return false;
        string? directory = Path.GetDirectoryName(Path.GetFullPath(documentPath));
        if (string.IsNullOrEmpty(directory)) return false;
        string prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate;
        try { candidate = Path.GetFullPath(Path.Combine(directory, reference.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        // Trailing dots and spaces are silently dropped by Windows path normalization: refuse instead of guessing.
        string leaf = Path.GetFileName(candidate);
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || leaf.Length == 0
            || reference.Split('/', '\\').Any(part => part.Length > 0 && part != "." && part != ".." && (part.EndsWith('.') || part.EndsWith(' '))))
            return false;
        fullPath = candidate;
        return true;
    }
}
