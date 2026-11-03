using PowerTrove.Abstractions;

namespace PowerTrove.Desktop;

/// <summary>
/// Opens the visual OCR reader only for a single installed, locally available raster.
/// Batches keep the pack's existing TXT/clipboard options; a screenshot saved to a
/// local PNG goes through the same path as any other image.
/// </summary>
internal static class OcrReaderInput
{
    public const string ActionId = "powertrove.image.to-text";
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".gif" };

    public static bool CanOpen(string actionId, ContextSnapshot? snapshot) =>
        actionId == ActionId &&
        snapshot is { Status: ContextStatus.Available, IsStale: false, Content: FileSelection { Files.Count: 1 } files } &&
        files.Files[0] is { IsDirectory: false, IsAccessible: true, IsAvailableLocally: true, Path: not null } file &&
        Supported.Contains(file.Extension);
}
