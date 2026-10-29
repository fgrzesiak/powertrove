namespace PowerTrove.Desktop;

internal static class ImageEffectsInput
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal) {
        "powertrove.image.crop-aspect", "powertrove.image.pad", "powertrove.image.grayscale", "powertrove.image.optimize-png",
        "powertrove.image.to-ico", "powertrove.image.watermark-text", "powertrove.image.watermark-image",
        "powertrove.image.collage", "powertrove.image.stitch", "powertrove.image.blur-faces", "powertrove.image.resize", "powertrove.image.png-to-svg" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".avif", ".heic", ".heif" };
    public static bool OpensDirectly(string id, ContextSnapshot snapshot) => id is not ("powertrove.image.to-ico" or "powertrove.image.png-to-svg") && CanOffer(id, snapshot);
    public static bool CanOffer(string id, ContextSnapshot snapshot) => Supported.Contains(id) &&
        snapshot.Status == ContextStatus.Available && snapshot.Content is FileSelection { Files.Count: > 0 } selection &&
        selection.Files.All(f => f.IsAvailableLocally && f.IsAccessible && !f.IsDirectory && Extensions.Contains(f.Extension ?? string.Empty)) &&
        (id != "powertrove.image.png-to-svg" || selection.Files.All(f => string.Equals(f.Extension, ".png", StringComparison.OrdinalIgnoreCase))) &&
        (id != "powertrove.image.resize" || selection.Files.Count == 1) &&
        (id == "powertrove.image.collage" ? selection.Files.Count is >= 2 and <= 100
            : id == "powertrove.image.stitch" ? selection.Files.Count is >= 2 and <= 50 : selection.Files.Count <= 1000);
}
