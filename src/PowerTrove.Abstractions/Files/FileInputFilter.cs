using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Abstractions.Files;

/// <summary>
/// Stable identifiers for content formats detected from file signatures.
/// Engines and verifiers exchange these strings instead of library-specific enums.
/// </summary>
public static class FileFormats
{
    public const string Jpeg = "jpeg";
    public const string Png = "png";
    public const string Gif = "gif";
    public const string Bmp = "bmp";
    public const string Tiff = "tiff";
    public const string WebP = "webp";
    public const string Avif = "avif";
    public const string Heic = "heic";
    public const string Ico = "ico";
    public const string Pdf = "pdf";
    public const string Zip = "zip";
    public const string Tar = "tar";
    public const string Gzip = "gzip";
    public const string Mp3 = "mp3";
    public const string Wav = "wav";
    public const string Flac = "flac";
    public const string Ogg = "ogg";
    public const string Aac = "aac";
    /// <summary>ISO base media file (MP4, M4A, MOV and relatives).</summary>
    public const string IsoMedia = "iso-media";
    /// <summary>Matroska and WebM.</summary>
    public const string Matroska = "matroska";
    public const string Avi = "avi";
    /// <summary>Advanced Systems Format (WMV, WMA).</summary>
    public const string Asf = "asf";
}

/// <summary>
/// Declares which selected items an action accepts: by extension for fast,
/// content-free matching, and by detected content format for execution-time
/// verification (extensions alone are never trusted for processing).
/// </summary>
public sealed class FileInputFilter
{
    private readonly HashSet<string>? _extensions;
    private readonly HashSet<string>? _formats;

    /// <param name="displayName">Plural, user-facing noun, e.g. "image files".</param>
    /// <param name="extensions">Accepted extensions including the dot; null accepts any file.</param>
    /// <param name="formats">Accepted <see cref="FileFormats"/> ids; null skips content verification.</param>
    /// <param name="acceptFiles">Whether regular files are accepted at all.</param>
    /// <param name="acceptDirectories">Whether directories are accepted.</param>
    public FileInputFilter(
        string displayName,
        IEnumerable<string>? extensions,
        IEnumerable<string>? formats,
        bool acceptFiles = true,
        bool acceptDirectories = false)
    {
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        _extensions = extensions is null ? null : new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        _formats = formats is null ? null : new HashSet<string>(formats, StringComparer.OrdinalIgnoreCase);
        AcceptFiles = acceptFiles;
        AcceptDirectories = acceptDirectories;
    }

    public string DisplayName { get; }
    public bool AcceptFiles { get; }
    public bool AcceptDirectories { get; }
    public IReadOnlyCollection<string>? Extensions => _extensions;
    public IReadOnlyCollection<string>? Formats => _formats;

    /// <summary>Static check that does not touch file content.</summary>
    public bool Accepts(SelectedFile file)
    {
        if (string.IsNullOrEmpty(file.Path) || !file.IsAvailableLocally || !file.IsAccessible)
            return false;

        if (file.IsDirectory)
            return AcceptDirectories;

        return AcceptFiles && (_extensions is null || _extensions.Contains(file.Extension ?? string.Empty));
    }

    public bool RequiresContentVerification(SelectedFile file) => !file.IsDirectory && _formats is not null;

    public bool AcceptsFormat(string? formatId) =>
        _formats is null || (formatId is not null && _formats.Contains(formatId));
}

public sealed record FileTypeVerification(bool IsAccepted, string? DetectedFormat, string? Reason)
{
    public static FileTypeVerification Accepted(string? format) => new(true, format, null);
    public static FileTypeVerification Rejected(string? format, string reason) => new(false, format, reason);
}

/// <summary>
/// Verifies the real content type of a file from its signature (magic bytes).
/// Reads only a small header and never the whole file.
/// </summary>
public interface IFileTypeVerifier
{
    ValueTask<FileTypeVerification> VerifyAsync(string path, FileInputFilter filter, CancellationToken cancellationToken);
}

/// <summary>Shared services every file action receives from the host.</summary>
public sealed record FileActionServices(
    IOutputFileSystem FileSystem,
    IOutputPlanner Planner,
    IFileTypeVerifier TypeVerifier)
{
    public System.Threading.AsyncLocal<string?> OutputDirectoryOverride { get; } = new();

    public static FileActionServices CreatePhysical(IFileTypeVerifier typeVerifier)
    {
        var fileSystem = new PhysicalOutputFileSystem();
        return new FileActionServices(fileSystem, new OutputPlanner(fileSystem), typeVerifier);
    }
}

/// <summary>
/// An expected failure with a message that is safe to show to the user: it must
/// not contain file contents, selected text or other captured data.
/// Any other exception type is reported with a generic message.
/// </summary>
public class FileProcessingException : Exception
{
    public FileProcessingException(string userMessage) : base(userMessage) { }
    public FileProcessingException(string userMessage, Exception innerException) : base(userMessage, innerException) { }
}

/// <summary>
/// An expected result where an action determines an item needs no changes
/// (e.g. already optimized, no faces found). It is counted as skipped, not failed.
/// </summary>
public class FileSkippedException : FileProcessingException
{
    public FileSkippedException() : base(Resources.SdkStrings.Error_ItemNeededNoChanges) { }
    public FileSkippedException(string message) : base(message) { }
}

/// <summary>Natural ("file2" before "file10"), culture-independent file name ordering.</summary>
public sealed class NaturalFileNameComparer : IComparer<string>
{
    public static NaturalFileNameComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                int cmp = a.SequenceCompareTo(b);
                if (cmp != 0) return cmp;
            }
            else
            {
                int cmp = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (cmp != 0) return cmp;
                i++;
                j++;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }

    public static IReadOnlyList<SelectedFile> Sort(IEnumerable<SelectedFile> files) =>
        files.OrderBy(f => f.Name, Instance).ToList();
}
