using System.Security.Cryptography;
using System.Text;
using FileKit.Detection;
using PowerTrove.Abstractions.Files;

namespace PowerTrove.Application.Documents;

public enum ImageImportFailure { None, NotAnImage, TooLarge, Unreadable, CannotCopy }

/// <param name="Copied">False when the image already was in the document's folder tree or an identical copy existed.</param>
public sealed record ImageImport(string? RelativePath, bool Copied, ImageImportFailure Failure)
{
    public override string ToString() => $"ImageImport {{ Copied = {Copied}, Failure = {Failure} }}";
}

/// <summary>
/// Makes an image the user picked usable by a document: images inside the document's folder tree are referenced
/// where they are; others are copied into the <see cref="FolderName"/> folder next to the document, so previews,
/// exports and anyone the document is shared with find them by a relative path. Never overwrites a file: an
/// identical file of the same name is reused, a different one gets a numbered name.
/// </summary>
public static class DocumentImages
{
    public const string FolderName = "images";

    public static async Task<ImageImport> ImportAsync(string documentPath, string imagePath, CancellationToken cancellationToken = default)
    {
        try
        {
            string document = Path.GetFullPath(documentPath), source = Path.GetFullPath(imagePath);
            string folder = Path.GetDirectoryName(document)!;
            var info = new FileInfo(source);
            if (!info.Exists) return new(null, false, ImageImportFailure.Unreadable);
            if (info.Length > CompanionResources.MaximumFileBytes) return new(null, false, ImageImportFailure.TooLarge);
            byte[] bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
            if (!IsImage(source, bytes)) return new(null, false, ImageImportFailure.NotAnImage);

            string inside = Path.GetRelativePath(folder, source).Replace('\\', '/');
            if (CompanionResources.TryResolve(document, inside, out _)) return new(inside, false, ImageImportFailure.None);

            string targetFolder = Path.Combine(folder, FolderName);
            Directory.CreateDirectory(targetFolder);
            string stem = SafeStem(Path.GetFileNameWithoutExtension(source));
            string extension = Path.GetExtension(source).ToLowerInvariant();
            byte[] hash = SHA256.HashData(bytes);
            for (int number = 1; number <= 999; number++)
            {
                string name = number == 1 ? stem + extension : $"{stem} ({number}){extension}";
                string target = Path.Combine(targetFolder, name);
                string relative = FolderName + "/" + name;
                if (File.Exists(target))
                {
                    // Inserting the same picture twice reuses the copy instead of piling up duplicates.
                    if (new FileInfo(target).Length == bytes.Length && SHA256.HashData(await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false)).AsSpan().SequenceEqual(hash))
                        return new(relative, false, ImageImportFailure.None);
                    continue;
                }
                try
                {
                    await using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);
                }
                catch (IOException) when (File.Exists(target)) { continue; }
                return CompanionResources.TryResolve(document, relative, out _) ? new(relative, true, ImageImportFailure.None) : new(null, false, ImageImportFailure.CannotCopy);
            }
            return new(null, false, ImageImportFailure.CannotCopy);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            return new(null, false, ex is UnauthorizedAccessException ? ImageImportFailure.CannotCopy : ImageImportFailure.Unreadable);
        }
    }

    private static bool IsImage(string path, byte[] bytes)
    {
        var format = FileSignature.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, FileSignature.HeaderLength))).Format;
        if (format is FileFormat.Png or FileFormat.Jpeg or FileFormat.Gif or FileFormat.WebP or FileFormat.Avif or FileFormat.Bmp or FileFormat.Ico) return true;
        if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return false;
        try { return new UTF8Encoding(false, true).GetString(bytes, 0, Math.Min(bytes.Length, 4096)).Contains("<svg", StringComparison.Ordinal); }
        catch (DecoderFallbackException) { return false; }
    }

    /// <summary>A file name that stays a valid, portable Markdown reference.</summary>
    private static string SafeStem(string stem)
    {
        var builder = new StringBuilder();
        foreach (char c in stem.Normalize(NormalizationForm.FormC))
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : c == ' ' ? '-' : '_');
        string result = builder.ToString().Trim('.', '-', '_');
        if (result.Length > 80) result = result[..80];
        return result.Length == 0 ? "image" : result;
    }
}
