using FileKit.Detection;
using PowerTrove.Abstractions.Files;

namespace PowerTrove.Application.Documents;

public sealed record PreviewResource(byte[] Content, string ContentType)
{
    public override string ToString() => $"PreviewResource {{ ContentType = {ContentType}, Length = {Content.Length} }}";
}

/// <summary>
/// What the host's document viewer serves for <see cref="DocumentPreviewData.ResourceBaseUri"/>: image files in the
/// open document's folder tree, by the same rule as pack reads (<see cref="CompanionResources"/>), verified by their
/// content. Everything else, including every network address, gets no response body.
/// </summary>
public static class DocumentPreviewResources
{
    public const long MaximumImageBytes = 25L * 1024 * 1024;

    /// <summary>The document's own address; the viewer adds a revision query to reload it.</summary>
    public static bool IsDocument(string uri) =>
        uri == DocumentPreviewData.ResourceBaseUri || uri.StartsWith(DocumentPreviewData.ResourceBaseUri + "?", StringComparison.Ordinal);

    public static async Task<PreviewResource?> TryReadAsync(string documentPath, string uri, CancellationToken cancellationToken = default)
    {
        if (!uri.StartsWith(DocumentPreviewData.ResourceBaseUri, StringComparison.Ordinal) || IsDocument(uri)) return null;
        string encoded = uri[DocumentPreviewData.ResourceBaseUri.Length..];
        int cut = encoded.IndexOfAny(['?', '#']);
        if (cut >= 0) encoded = encoded[..cut];
        string reference;
        try { reference = Uri.UnescapeDataString(encoded); }
        catch (UriFormatException) { return null; }
        if (!CompanionResources.TryResolve(documentPath, reference, out string path)) return null;
        try
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(documentPath))!;
            if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network) return null;
            for (string? current = path; current is not null && current.Length > folder.Length; current = Path.GetDirectoryName(current))
            {
                if (!File.Exists(current) && !Directory.Exists(current)) return null;
                const FileAttributes rejected = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;
                if ((File.GetAttributes(current) & rejected) != 0) return null;
            }
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumImageBytes) return null;
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            string? type = FileSignature.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, FileSignature.HeaderLength))).Format switch
            {
                FileFormat.Png => "image/png", FileFormat.Jpeg => "image/jpeg", FileFormat.Gif => "image/gif", FileFormat.WebP => "image/webp",
                FileFormat.Avif => "image/avif", FileFormat.Bmp => "image/bmp", FileFormat.Ico => "image/x-icon",
                _ => path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && LooksLikeSvg(bytes) ? "image/svg+xml" : null
            };
            return type is null ? null : new PreviewResource(bytes, type);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static bool LooksLikeSvg(byte[] bytes)
    {
        if (bytes.Length > 4L * 1024 * 1024) return false;
        try { return new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, Math.Min(bytes.Length, 4096)).Contains("<svg", StringComparison.Ordinal); }
        catch (System.Text.DecoderFallbackException) { return false; }
    }
}
